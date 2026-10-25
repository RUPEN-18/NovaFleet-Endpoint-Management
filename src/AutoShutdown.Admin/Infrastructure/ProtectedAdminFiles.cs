using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
namespace AutoShutdown.Admin.Infrastructure;
internal static class ProtectedAdminFiles
{
    internal static void ProtectTree(string root)
    {
        using var identity=WindowsIdentity.GetCurrent();
        var sids=new[]{identity.User!,new SecurityIdentifier("S-1-5-18"),new SecurityIdentifier("S-1-5-32-544")};
        var entries=new List<string>{root};
        for(var i=0;i<entries.Count;i++) {
            var entry=entries[i];var attributes=File.GetAttributes(entry);
            if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Administrator data must not contain symbolic links.");
            if((attributes&FileAttributes.Directory)!=0){
                var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);acl.SetOwner(identity.User!);
                foreach(var sid in sids)acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
                new DirectoryInfo(entry).SetAccessControl(acl);entries.AddRange(Directory.EnumerateFileSystemEntries(entry));
            } else{
                var acl=new FileSecurity();acl.SetAccessRuleProtection(true,false);acl.SetOwner(identity.User!);
                foreach(var sid in sids)acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,AccessControlType.Allow));
                new FileInfo(entry).SetAccessControl(acl);
            }
        }
    }
}
