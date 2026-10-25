using AutoShutdown.Admin.Services;
using AutoShutdown.Shared;
using Microsoft.Win32;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
namespace AutoShutdown.Admin;
public partial class LoginWindow:Window
{
    private readonly AuthApiClient _auth;private bool _setup;
    public LoginSession? Session{get;private set;}
    public LoginWindow(AuthApiClient auth)
    {
        InitializeComponent();_auth=auth;
        Loaded+=async(_,_)=>{try{_setup=await _auth.NeedsSetupAsync();SetupFields.Visibility=_setup?Visibility.Visible:Visibility.Collapsed;
            IntroText.Text=_setup?"Create the first Super Admin account for this Windows profile. No default password is provided.":"Sign in to manage your client PCs.";
            LoginButton.Content=_setup?"Create Super Admin":"Sign in";UsernameBox.Focus();}
            catch(Exception ex){ErrorText.Text=ex.Message;LoginButton.IsEnabled=false;}};
    }
    private async void Login_Click(object sender,RoutedEventArgs e)
    {
        LoginButton.IsEnabled=false;ErrorText.Text="";
        try{
            var request=new SetupRequest(UsernameBox.Text.Trim(),DisplayNameBox.Text.Trim(),PasswordBox.Password);
            Session=_setup?await _auth.SetupAsync(request):await _auth.LoginAsync(new(request.Username,request.Password));
            PasswordBox.Clear();
            if(Session.RecoveryCode.Length>0){
                var save=new SaveFileDialog{Title="Save your private Super Admin recovery code",Filter="Recovery text|*.txt",FileName="LT-Admin-Recovery.txt",
                    InitialDirectory=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)};
                if(save.ShowDialog(this)==true){
                    var temporary=save.FileName+"."+Guid.NewGuid().ToString("N")+".tmp";
                    using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                        using var identity=WindowsIdentity.GetCurrent();var acl=new FileSecurity();acl.SetAccessRuleProtection(true,false);acl.SetOwner(identity.User!);
                        foreach(var sid in new[]{identity.User!,new SecurityIdentifier("S-1-5-18"),new SecurityIdentifier("S-1-5-32-544")})acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,AccessControlType.Allow));
                        new FileInfo(temporary).SetAccessControl(acl);
                        using var writer=new StreamWriter(output);writer.Write($"L&T Admin recovery\nUsername: {Session.Identity.Username}\nRecovery code: {Session.RecoveryCode}\nKeep this file private. It can reset this Super Admin password.\n");
                    }
                    File.Move(temporary,save.FileName,true);
                } else MessageBox.Show(this,"Recovery code (store privately):\n\n"+Session.RecoveryCode,"Super Admin recovery",MessageBoxButton.OK,MessageBoxImage.Information);
            }
            DialogResult=true;
        }catch(Exception ex){
            ErrorText.Text=ex.Message;
            try{_setup=await _auth.NeedsSetupAsync();SetupFields.Visibility=_setup?Visibility.Visible:Visibility.Collapsed;LoginButton.Content=_setup?"Create Super Admin":"Sign in";}
            catch(Exception statusError){ErrorText.Text="Login connection unavailable: "+statusError.Message;}
        }
        finally{LoginButton.IsEnabled=true;}
    }
    private async void Recovery_Click(object sender,RoutedEventArgs e)
    {try{await _auth.RecoverAsync(new(UsernameBox.Text.Trim(),RecoveryBox.Password,NewPasswordBox.Password));RecoveryBox.Clear();NewPasswordBox.Clear();ErrorText.Text="Password reset. Sign in with your new password.";}catch(Exception ex){ErrorText.Text=ex.Message;}}
}
