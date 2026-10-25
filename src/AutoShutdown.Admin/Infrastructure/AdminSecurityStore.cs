using AutoShutdown.Shared;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
namespace AutoShutdown.Admin.Infrastructure;

public sealed class AdminSecurityStore
{
    private sealed record Account(AdminIdentity Identity,string PasswordHash,string RecoveryHash="");
    private sealed record Session(string AccountId,DateTimeOffset ExpiresUtc);
    private sealed record Failure(int Count,DateTimeOffset Until);
    private sealed record Database(int SchemaVersion,List<Account> Accounts);
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly Dictionary<string,Session> _sessions=new();
    private readonly Dictionary<string,Failure> _failures=new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly string _auditPath;
    private Database _data;
    private readonly string _dummy=PasswordHash.Create(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    private readonly Func<DateTimeOffset> _clock;
    public AdminSecurityStore(string root,Func<DateTimeOffset>? clock=null)
    {
        Directory.CreateDirectory(root); _clock=clock??(()=>DateTimeOffset.UtcNow);
        _path=Path.Combine(root,"admin-accounts.json"); _auditPath=Path.Combine(root,"admin-audit.jsonl");
        _data=File.Exists(_path)?JsonSerializer.Deserialize<Database>(File.ReadAllText(_path))
            ??throw new InvalidDataException("Administrator database is invalid; restore its private backup."):new(1,new());
        if(_data.SchemaVersion!=1 || _data.Accounts is null) throw new InvalidDataException("Unsupported or invalid administrator database version.");
        if(_data.Accounts.Select(account=>account.Identity.Id).Distinct(StringComparer.Ordinal).Count()!=_data.Accounts.Count
            || _data.Accounts.Select(account=>account.Identity.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=_data.Accounts.Count)
            throw new InvalidDataException("Administrator database contains duplicate account identities. Preserve the backup for recovery.");
    }
    public async Task<AdminIdentity?> IdentityAsync(string id)
    {await _gate.WaitAsync();try{return _data.Accounts.FirstOrDefault(x=>x.Identity.Id==id)?.Identity;}finally{_gate.Release();}}
    public bool NeedsSetup=>_data.Accounts.Count==0;
    private async Task Save(Database next)
    {
        if(File.Exists(_path) && !File.Exists(_path+".migration-backup")) File.Copy(_path,_path+".migration-backup");
        await File.WriteAllTextAsync(_path+".tmp",JsonSerializer.Serialize(next));
        File.Move(_path+".tmp",_path,true); _data=next;
    }
    private Task AuditUnsafe(AuditEntry entry)=>File.AppendAllTextAsync(_auditPath,JsonSerializer.Serialize(entry)+Environment.NewLine);
    public async Task AuditAsync(AdminIdentity? actor,string action,string result,string details="",string deviceId="",string computer="",string software="",string version="",string previous="",string current="")
    {
        await _gate.WaitAsync(); try {await AuditUnsafe(new(){At=_clock(),AdminId=actor?.Id??"",Username=actor?.Username??"SYSTEM",DisplayName=actor?.DisplayName??"System",
            Action=action,Result=result,Details=details,DeviceId=deviceId,ComputerName=computer,Software=software,Version=version,Previous=previous,Current=current});}
        finally {_gate.Release();}
    }
    public async Task<List<AuditEntry>> AuditAsync(AdminIdentity actor)
    {
        if(!actor.Can(AdminPermission.ReadLogs)) throw new UnauthorizedAccessException("Activity log permission required.");
        await _gate.WaitAsync(); try {
            if(!File.Exists(_auditPath)) return new();
            var result=new Queue<AuditEntry>();
            foreach(var line in File.ReadLines(_auditPath)) {
                if(string.IsNullOrWhiteSpace(line))continue;
                try {var e=JsonSerializer.Deserialize<AuditEntry>(line); if(e is not null && (actor.IsSuper || e.AdminId==actor.Id || e.DeviceId.Length>0 && actor.CanDevice(e.DeviceId))){result.Enqueue(e);if(result.Count>2000)result.Dequeue();}}
                catch(JsonException){ throw new InvalidDataException("Audit history contains an incomplete record. Preserve the file for recovery."); }
            }
            return result.Reverse().ToList();
        } finally {_gate.Release();}
    }
    public async Task<LoginSession> SetupAsync(SetupRequest request)
    {
        PasswordHash.ValidateUsername(request.Username); ValidateDisplay(request.DisplayName); PasswordHash.Validate(request.Password);
        await _gate.WaitAsync(); try {
            if(!NeedsSetup)throw new InvalidOperationException("First administrator already exists.");
            var identity=new AdminIdentity(Guid.NewGuid().ToString("N"),request.Username,request.DisplayName,"SuperAdmin",AdminPermission.All,new());
            var recovery=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await Save(_data with {Accounts=new(){new(identity,PasswordHash.Create(request.Password),PasswordHash.Digest(recovery))}});
            await AuditUnsafe(new(){At=_clock(),AdminId=identity.Id,Username=identity.Username,DisplayName=identity.DisplayName,Action="FIRST_SUPER_ADMIN",Result="Created"});
            return Issue(identity) with {RecoveryCode=recovery};
        } finally {_gate.Release();}
    }
    private LoginSession Issue(AdminIdentity identity)
    {
        var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); var expires=_clock().AddMinutes(30);
        foreach(var stale in _sessions.Where(x=>x.Value.ExpiresUtc<=_clock()).Select(x=>x.Key).ToArray()) _sessions.Remove(stale);
        _sessions[PasswordHash.Digest(token)]=new(identity.Id,expires); return new(token,identity,expires);
    }
    private void CheckRate(string key)
    {
        if(_failures.TryGetValue(key,out var fail) && fail.Until>_clock() && fail.Count>=5)
            throw new UnauthorizedAccessException("Too many attempts. Try again in 15 minutes.");
    }
    private void Failed(string key)
    {
        _failures.TryGetValue(key,out var f);
        _failures[key]=new(f is not null && f.Until>_clock()?f.Count+1:1,_clock().AddMinutes(15));
        if(_failures.Count>1024) foreach(var expired in _failures.Where(x=>x.Value.Until<=_clock()).Select(x=>x.Key).ToArray())_failures.Remove(expired);
    }
    public async Task<LoginSession> LoginAsync(LoginRequest request)
    {
        if(request.Username is null || request.Username.Length>64 || request.Password is null || request.Password.Length>128) throw new UnauthorizedAccessException("Invalid username or password.");
        await _gate.WaitAsync();try {
            CheckRate("GLOBAL"); CheckRate(request.Username);
            var account=_data.Accounts.FirstOrDefault(x=>string.Equals(x.Identity.Username,request.Username,StringComparison.OrdinalIgnoreCase));
            var verified=PasswordHash.Verify(request.Password,account?.PasswordHash??_dummy);
            if(!verified || account?.Identity.Enabled!=true) {
                Failed(request.Username); Failed("GLOBAL");
                await AuditUnsafe(new(){At=_clock(),Username=request.Username,Action="LOGIN",Result="Failed",Details="Invalid username or password."});
                throw new UnauthorizedAccessException("Invalid username or password.");
            }
            _failures.Remove(request.Username); _failures.Remove("GLOBAL");
            await AuditUnsafe(new(){At=_clock(),AdminId=account.Identity.Id,Username=account.Identity.Username,DisplayName=account.Identity.DisplayName,Action="LOGIN",Result="Success"});
            return Issue(account.Identity);
        }finally{_gate.Release();}
    }
    public async Task<AdminIdentity?> ResolveAsync(string token)
    {
        if(token.Length!=64)return null;
        await _gate.WaitAsync();try {
            var key=PasswordHash.Digest(token);
            if(!_sessions.TryGetValue(key,out var session) || session.ExpiresUtc<=_clock()){_sessions.Remove(key);return null;}
            return _data.Accounts.FirstOrDefault(x=>x.Identity.Id==session.AccountId && x.Identity.Enabled)?.Identity;
        }finally{_gate.Release();}
    }
    public async Task LogoutAsync(string token)
    {
        var actor=await ResolveAsync(token); await _gate.WaitAsync();try{_sessions.Remove(PasswordHash.Digest(token));}finally{_gate.Release();}
        if(actor is not null)await AuditAsync(actor,"LOGOUT","Success");
    }
    public async Task<List<AdminIdentity>> AccountsAsync(AdminIdentity actor)
    {RequireSuper(actor);await _gate.WaitAsync();try{return _data.Accounts.Select(x=>x.Identity).ToList();}finally{_gate.Release();}}
    private static void RequireSuper(AdminIdentity actor){if(!actor.IsSuper)throw new UnauthorizedAccessException("Super Admin permission required.");}
    private static void ValidateDisplay(string value){if(string.IsNullOrWhiteSpace(value) || value.Length>120 || value.Any(char.IsControl))throw new ArgumentException("Enter a display name of 1–120 characters.");}
    public async Task<AdminIdentity> AccountAsync(AdminIdentity actor,AccountChange change)
    {
        RequireSuper(actor);
        if(change.Id is null || change.Password is null)throw new ArgumentException("Invalid account change.");
        PasswordHash.ValidateUsername(change.Username);ValidateDisplay(change.DisplayName);
        if(change.DeviceIds is null || change.Role is not ("Admin" or "SuperAdmin") || (change.Permissions & ~AdminPermission.All)!=0 || change.DeviceIds.Count>1000 || change.DeviceIds.Any(x=>!Guid.TryParseExact(x,"N",out _)))throw new ArgumentException("Invalid role, permission, or device ID.");
        await _gate.WaitAsync();try {
            var old=_data.Accounts.FirstOrDefault(x=>x.Identity.Id==change.Id);
            if(change.Id.Length>0 && old is null)throw new ArgumentException("Administrator account no longer exists. Refresh the list.");
            if(_data.Accounts.Any(x=>x.Identity.Id!=change.Id && string.Equals(x.Identity.Username,change.Username,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("That username already exists.");
            if(old is not null && old.Identity.IsSuper && (!change.Enabled || change.Role!="SuperAdmin") && _data.Accounts.Count(x=>x.Identity.IsSuper)==1)throw new InvalidOperationException("Keep at least one enabled Super Admin.");
            if(old is null || change.Password.Length>0)PasswordHash.Validate(change.Password);
            var identity=new AdminIdentity(old?.Identity.Id??Guid.NewGuid().ToString("N"),change.Username,change.DisplayName,change.Role,
                change.Role=="SuperAdmin"?AdminPermission.All:change.Permissions & ~AdminPermission.Manage,change.DeviceIds.Distinct().ToList(),change.Enabled);
            var next=new Account(identity,change.Password.Length>0?PasswordHash.Create(change.Password):old!.PasswordHash,old?.RecoveryHash??"");
            await Save(_data with {Accounts=_data.Accounts.Where(x=>x.Identity.Id!=identity.Id).Append(next).ToList()});
            foreach(var key in _sessions.Where(x=>x.Value.AccountId==identity.Id).Select(x=>x.Key).ToArray())_sessions.Remove(key);
            await AuditUnsafe(new(){At=_clock(),AdminId=actor.Id,Username=actor.Username,DisplayName=actor.DisplayName,Action="ACCOUNT_CHANGED",Result="Success",Details=identity.Label,
                Previous=old is null?"":JsonSerializer.Serialize(old.Identity),Current=JsonSerializer.Serialize(identity)});
            return identity;
        }finally{_gate.Release();}
    }
    public async Task RecoverAsync(RecoveryRequest request)
    {
        PasswordHash.Validate(request.NewPassword);await _gate.WaitAsync();try{
            CheckRate("RECOVERY");
            var account=_data.Accounts.FirstOrDefault(x=>x.Identity.IsSuper && string.Equals(x.Identity.Username,request.Username,StringComparison.OrdinalIgnoreCase));
            var digest=PasswordHash.Digest(request.RecoveryCode??"");
            if(account is null || account.RecoveryHash.Length!=64 || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(digest),Convert.FromHexString(account.RecoveryHash))){Failed("RECOVERY");throw new UnauthorizedAccessException("Invalid recovery information.");}
            await Save(_data with {Accounts=_data.Accounts.Select(x=>x.Identity.Id==account.Identity.Id?x with{PasswordHash=PasswordHash.Create(request.NewPassword)}:x).ToList()});
            foreach(var key in _sessions.Where(x=>x.Value.AccountId==account.Identity.Id).Select(x=>x.Key).ToArray())_sessions.Remove(key);
            _failures.Remove("RECOVERY");await AuditUnsafe(new(){At=_clock(),AdminId=account.Identity.Id,Username=account.Identity.Username,DisplayName=account.Identity.DisplayName,Action="PASSWORD_RECOVERY",Result="Success"});
        }finally{_gate.Release();}
    }
    public static void ValidatePolicy(GlobalSettings settings)
    {
        if(!double.IsFinite(settings.IdleScreenMinutes) || settings.IdleScreenMinutes<=0 || settings.IdleScreenMinutes>10080 || !double.IsFinite(settings.ShutdownHours) || settings.ShutdownHours<=0 || settings.ShutdownHours>168 || settings.ShutdownWarningSeconds is <1 or >3600 || (settings.IdleMessage is null || settings.IdleMessage.Length>4000))
            throw new ArgumentException("Enter a valid policy: positive idle delay up to 10080 minutes, positive shutdown delay up to 168 hours, and warning from 1 to 3600 seconds.");
    }
}
