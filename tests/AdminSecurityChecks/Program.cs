using AutoShutdown.Admin.Infrastructure;
using AutoShutdown.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
var count=0;
void Check(bool passed,string name){if(!passed)throw new Exception("FAIL: "+name);Console.WriteLine("PASS: "+name);count++;}
async Task Reject(Func<Task> action,string name){try{await action();}catch(Exception e)when(e is ArgumentException or UnauthorizedAccessException or InvalidOperationException){Check(true,name);return;}throw new Exception("FAIL: "+name);}
var root=Path.Combine(Path.GetTempPath(),"LTAdminChecks-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
try{
    var now=DateTimeOffset.UtcNow;
    using var rsa=RSA.Create(2048);var req=new CertificateRequest("CN=Admin Login Test",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
    var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");san.AddIpAddress(System.Net.IPAddress.Loopback);
    req.CertificateExtensions.Add(san.Build());
    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
    req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
    req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new("1.3.6.1.5.5.7.3.1")},true));
    using var generated=req.CreateSelfSigned(now.AddDays(-1),now.AddDays(1));
    var certificatePassword=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    using var cert=X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx,certificatePassword),certificatePassword,
        OperatingSystem.IsWindows()?X509KeyStorageFlags.UserKeySet:X509KeyStorageFlags.EphemeralKeySet);
    var pin=cert.GetCertHashString(HashAlgorithmName.SHA256);
    var security=new AdminSecurityStore(root,()=>now);
    Check(security.NeedsSetup,"new store has no default password or account");
    await Reject(()=>security.SetupAsync(new("root","Root","short")),"short password rejected");
    var initial=await security.SetupAsync(new("root","Chennai Admin","a long unique password 2026"));
    Check(initial.Identity.IsSuper&&initial.RecoveryCode.Length==64,"first account is Super Admin with private recovery code");
    await Reject(()=>security.SetupAsync(new("another","Another","a long unique password 2026")),"first-run setup cannot create a second super account");
    Check(initial.Identity.Id.Length==32 && initial.Identity.Username=="root","account identity is separate from its display name");
    Check(!initial.Token.Contains(initial.Identity.Id,StringComparison.Ordinal),"session tokens are random rather than account IDs");
    var hash=PasswordHash.Create("long password for hash test");
    Check(PasswordHash.Verify("long password for hash test",hash),"PBKDF2 password hash verifies correct password");
    Check(!PasswordHash.Verify("wrong password",hash),"wrong password rejected");
    Check(hash!=PasswordHash.Create("long password for hash test"),"each password receives a random salt");
    Check(!PasswordHash.Verify("any","pbkdf2-sha256$1$broken$broken"),"malformed or weak password hashes rejected");
    var device=Guid.NewGuid().ToString("N");var other=Guid.NewGuid().ToString("N");
    var admin=await security.AccountAsync(initial.Identity,new("","admin01","Chennai Admin","Admin",AdminPermission.Install|AdminPermission.ReadLogs,new(){device},true,"second long unique password"));
    Check(admin.Id!=initial.Identity.Id&&admin.DisplayName==initial.Identity.DisplayName,"similar display names have unique account IDs");
    Check(admin.Can(AdminPermission.Install)&&!admin.Can(AdminPermission.Manage)&&!admin.Can(AdminPermission.Automation),"ordinary Admin has only granted permissions");
    Check(admin.CanDevice(device)&&!admin.CanDevice(other),"device assignments restrict target scope");
    await Reject(()=>security.AccountAsync(admin,new("","bad","Bad","SuperAdmin",AdminPermission.All,new(),true,"second long unique password")),"ordinary Admin cannot create administrator accounts");
    await Reject(()=>security.AccountAsync(initial.Identity,new("","ADMIN01","Other","Admin",AdminPermission.Install,new(),true,"third long unique password")),"case-insensitive duplicate usernames rejected");
    await Reject(()=>security.AccountAsync(initial.Identity,new(initial.Identity.Id,"root","Root","Admin",AdminPermission.Install,new(),true)),"last enabled Super Admin cannot be demoted");
    var session=await security.LoginAsync(new("admin01","second long unique password"));
    Check((await security.ResolveAsync(session.Token))?.Id==admin.Id,"login issues a usable session");
    await security.LogoutAsync(session.Token);Check(await security.ResolveAsync(session.Token) is null,"logout revokes the session");
    session=await security.LoginAsync(new("admin01","second long unique password"));now=now.AddMinutes(31);
    Check(await security.ResolveAsync(session.Token) is null,"session expires after thirty minutes");
    for(var i=0;i<5;i++)await Reject(()=>security.LoginAsync(new("unknown","bad")),"failed login recorded "+i);
    await Reject(()=>security.LoginAsync(new("root","a long unique password 2026")),"brute-force lockout blocks further attempts");
    now=now.AddMinutes(16);Check((await security.LoginAsync(new("root","a long unique password 2026"))).Identity.IsSuper,"lockout expires after fifteen minutes");
    var beforeRestart=await security.LoginAsync(new("root","a long unique password 2026"));
    var reopened=new AdminSecurityStore(root,()=>now);
    Check(!reopened.NeedsSetup && (await reopened.IdentityAsync(admin.Id))?.Username==admin.Username,"administrator accounts survive reopening the store");
    Check(await reopened.ResolveAsync(beforeRestart.Token) is null,"sessions are not restored after server restart");
    var current=await security.LoginAsync(new("admin01","second long unique password"));
    await security.AccountAsync(initial.Identity,new(admin.Id,admin.Username,"Updated name","Admin",AdminPermission.Install,new(){device},true));
    Check(await security.ResolveAsync(current.Token) is null,"account edits revoke existing sessions");
    Check((await security.IdentityAsync(admin.Id))?.DisplayName=="Updated name","display-name edits preserve account identity");
    await Reject(()=>security.AccountAsync(initial.Identity,new(Guid.NewGuid().ToString("N"),"missing","Missing","Admin",AdminPermission.Install,new(),true,"a valid long password")),"editing an unknown account does not create a replacement account");
    var lockedSession=await security.LoginAsync(new("admin01","second long unique password"));
    await security.AccountAsync(initial.Identity,new(admin.Id,admin.Username,admin.DisplayName,"Admin",AdminPermission.Install,new(){device},false));
    Check(await security.ResolveAsync(lockedSession.Token) is null,"disabling an account immediately revokes its session");
    await Reject(()=>security.LoginAsync(new("admin01","second long unique password")),"disabled login rejected");
    await security.AccountAsync(initial.Identity,new(admin.Id,admin.Username,admin.DisplayName,"Admin",AdminPermission.Install,new(){device},true));
    var rootSession=await security.LoginAsync(new("root","a long unique password 2026"));
    await Reject(()=>security.RecoverAsync(new("root","wrong code","replacement long password")),"invalid recovery code rejected");
    await security.RecoverAsync(new("root",initial.RecoveryCode,"replacement long password"));
    Check(await security.ResolveAsync(rootSession.Token) is null,"password recovery revokes old sessions");
    Check((await security.LoginAsync(new("root","replacement long password"))).Identity.Id==initial.Identity.Id,"recovery preserves account identity");
    var audit=await security.AuditAsync(initial.Identity);
    Check(audit.Any(x=>x.Action=="ACCOUNT_CHANGED"&&x.AdminId==initial.Identity.Id),"account audit records the initiating administrator ID");
    var accountBytes=File.ReadAllText(Path.Combine(root,"admin-accounts.json"));var auditBytes=File.ReadAllText(Path.Combine(root,"admin-audit.jsonl"));
    Check(!accountBytes.Contains("replacement long password")&&!accountBytes.Contains(initial.RecoveryCode),"passwords and recovery codes are not stored in plaintext");
    Check(!auditBytes.Contains("replacement long password")&&!auditBytes.Contains(initial.RecoveryCode)&&!auditBytes.Contains(rootSession.Token),"audit records exclude passwords, recovery codes and session tokens");
    await security.AccountAsync(initial.Identity,new(admin.Id,admin.Username,admin.DisplayName,"Admin",AdminPermission.Install,new(){device},false));
    await Reject(()=>security.LoginAsync(new("admin01","second long unique password")),"disabled accounts cannot log in");
    Check(AdminSessionEndpoints.ControlPermission("/API/DEVICES/","PUT")==AdminPermission.Manage,"case and trailing slash cannot bypass device-management permission");
    Check(AdminSessionEndpoints.ControlPermission("/api/unrecognized","POST")==AdminPermission.Manage,"unknown API mutations fail closed");
    Check(AdminSessionEndpoints.InstallPermission("/ADMIN/NETWORK/","POST")==AdminPermission.Manage,"installer routes keep case-insensitive permission enforcement");
    var legacyRoot=Path.Combine(root,"legacy-installation");Directory.CreateDirectory(legacyRoot);
    var serviceCredential=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    var controlCredential=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    var legacyJobId=Guid.NewGuid().ToString("N");
    var legacyPackage=new ApprovedPackage{Sha256=new string('A',64),Length=1024};
    var legacyData=new{
        devices=new[]{new{id=device,computerName="CLIENT-02",tokenHash=InstallStore.HashToken(serviceCredential),controlTokenHash=InstallStore.HashToken(controlCredential),lastSeenUtc=now,serviceVersion="0.5.0"}},
        package=legacyPackage,
        jobs=new[]{new{id=legacyJobId,deviceId=device,computerName="CLIENT-02",package=legacyPackage,state="Installed",installedVersion="26.04",createdUtc=now,updatedUtc=now,details="Previously installed"}}
    };
    File.WriteAllText(Path.Combine(legacyRoot,"install-jobs.json"),JsonSerializer.Serialize(legacyData));
    var legacyStore=new InstallStore(legacyRoot);
    Check(await legacyStore.AuthenticateAsync(device,serviceCredential)==device,"existing service pairing credentials survive migration");
    Check(await legacyStore.AuthenticateComputerAsync(device,controlCredential)=="CLIENT-02","existing Client control credentials survive migration");
    var legacyOverview=await legacyStore.OverviewAsync();
    Check(legacyOverview.Jobs.Single().Id==legacyJobId && legacyOverview.Jobs.Single().State==InstallState.Installed,"old installation history remains readable");
    Check(legacyOverview.Jobs.Single().AdminId.Length==0,"old installation jobs are not attributed to an invented administrator");
    Check(File.Exists(Path.Combine(legacyRoot,"install-jobs.json.pre-step123.bak")),"pre-update installation data is backed up");
    // Real TLS requests exercise the same bootstrap/session/permission gate used by Admin.
    var builder=Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions{Args=Array.Empty<string>()});
    builder.WebHost.ConfigureKestrel(options=>options.Listen(System.Net.IPAddress.Loopback,0,endpoint=>endpoint.UseHttps(cert)));
    var web=builder.Build();var port=0;var bootstrap=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    web.Use(async (Microsoft.AspNetCore.Http.HttpContext context,Microsoft.AspNetCore.Http.RequestDelegate next)=>{
        if(!AdminSessionEndpoints.CheckBootstrap(context,bootstrap,port)){context.Response.StatusCode=403;return;}
        if(context.Request.Path.StartsWithSegments("/admin") && !await AdminSessionEndpoints.Authorize(context,security,AdminSessionEndpoints.InstallPermission(context.Request.Path,context.Request.Method)))return;
        if(context.Request.Path.StartsWithSegments("/api") && !await AdminSessionEndpoints.Authorize(context,security,AdminSessionEndpoints.ControlPermission(context.Request.Path,context.Request.Method)))return;
        await next(context);
    });
    AdminSessionEndpoints.MapAuthentication(web,security);
    AdminSessionEndpoints.MapManagement(web,security,legacyStore);
    web.MapGet("/admin/overview",()=>Microsoft.AspNetCore.Http.Results.Ok(new{ready=true}));
    web.MapPost("/admin/network",()=>Microsoft.AspNetCore.Http.Results.Ok());
    web.MapPost("/admin/jobs",()=>Microsoft.AspNetCore.Http.Results.Ok());
    await web.StartAsync();
    try{
        var addresses=web.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses;
        var address=new Uri(addresses.Single());port=address.Port;
        var wrongPin=(pin[0]=='0'?"1":"0")+pin[1..];
        using(var untrusted=DeploymentTransport.Create(address,wrongPin)){
            untrusted.Timeout=TimeSpan.FromSeconds(10);var rejected=false;
            try{using var response=await untrusted.GetAsync("admin/overview");}
            catch(System.Net.Http.HttpRequestException){rejected=true;}
            Check(rejected,"TLS transport still rejects an incorrect certificate pin");
        }
        using var http=DeploymentTransport.Create(address,pin);
        using(var denied=await http.GetAsync("admin/overview"))Check((int)denied.StatusCode==403,"TLS admin endpoint rejects missing bootstrap credential");
        http.DefaultRequestHeaders.Add("X-Nova-Admin-Token",bootstrap);
        using(var denied=await http.GetAsync("admin/overview"))Check((int)denied.StatusCode==401,"bootstrap credential alone does not authorize Admin operations");
        var limited=await security.AccountAsync(initial.Identity,new("","limited","Limited Admin","Admin",AdminPermission.Install,new(){device},true,"limited long unique password"));
        var limitedSession=await security.LoginAsync(new(limited.Username,"limited long unique password"));
        http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",limitedSession.Token);
        using(var allowed=await http.GetAsync("admin/overview"))Check(allowed.IsSuccessStatusCode,"authenticated administrator can read assigned overview");
        using(var denied=await http.PostAsync("admin/network",null))Check((int)denied.StatusCode==403,"ordinary Admin cannot change network settings through API");
        using(var allowed=await http.PostAsync("admin/jobs",null))Check(allowed.IsSuccessStatusCode,"installation permission authorizes installation route");
        using(var denied=await http.GetAsync("api/accounts"))Check((int)denied.StatusCode==403,"ordinary Admin cannot read administrator accounts through API");
        using(var denied=await http.GetAsync("api/audit"))Check((int)denied.StatusCode==403,"an account without log permission cannot read audit records");
        using(var allowed=await http.GetAsync("api/devices")){
            allowed.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await allowed.Content.ReadAsStringAsync());
            Check(json.RootElement.ValueKind==JsonValueKind.Array && json.RootElement.GetArrayLength()==1 && json.RootElement[0].GetProperty("id").GetString()==device,
                "device endpoint writes a JSON array containing the assigned PC");
        }
        var superSession=await security.LoginAsync(new("root","replacement long password"));
        http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",superSession.Token);
        using(var allowed=await http.GetAsync("api/accounts")){
            allowed.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await allowed.Content.ReadAsStringAsync());
            Check(json.RootElement.ValueKind==JsonValueKind.Array && json.RootElement.EnumerateArray().Any(account=>account.GetProperty("id").GetString()==initial.Identity.Id),
                "account endpoint writes JSON instead of discarding its result");
        }
        using(var allowed=await http.GetAsync("api/audit")){
            allowed.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await allowed.Content.ReadAsStringAsync());
            Check(json.RootElement.ValueKind==JsonValueKind.Array && json.RootElement.GetArrayLength()>0,"audit endpoint writes nonempty JSON records");
        }
        using(var loggedOut=await http.PostAsync("auth/logout",null))Check(loggedOut.IsSuccessStatusCode,"logout endpoint returns an HTTP success response");
        using(var denied=await http.GetAsync("api/accounts"))Check((int)denied.StatusCode==401,"logout endpoint revokes the Super Admin API session");
        http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",limitedSession.Token);
        await security.LogoutAsync(limitedSession.Token);
        using(var denied=await http.GetAsync("admin/overview"))Check((int)denied.StatusCode==401,"logged-out bearer token is rejected by API");
    }finally{await web.StopAsync();await web.DisposeAsync();}
    Console.WriteLine($"All {count} administrator security checks passed.");
}finally{Directory.Delete(root,true);}
