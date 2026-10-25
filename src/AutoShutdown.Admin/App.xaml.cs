using AutoShutdown.Admin.Infrastructure;
using AutoShutdown.Admin.Services;
using AutoShutdown.Shared;
using System.Windows;

namespace AutoShutdown.Admin;

public partial class App : Application
{
    private System.Threading.Mutex? _instance;
    private AdminServer? _server;
    private AdminApiClient? _api;
    private InstallServer? _installServer;
    private InstallApiClient? _installApi;
    private AuthApiClient? _auth;
    private LoginSession? _session;
    private bool _logoutBusy;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => AppDiagnostics.Record("SystemA", "UI_EXCEPTION", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception) AppDiagnostics.Record("SystemA", "UNHANDLED_EXCEPTION", exception);
        };
        try
        {
            using var identity=System.Security.Principal.WindowsIdentity.GetCurrent();
            _instance=new System.Threading.Mutex(true,"Global\\LTAdmin-"+identity.User!.Value,out var created);
            if(!created){MessageBox.Show("L&T Admin is already running for this Windows user.","L&T Admin");Shutdown();return;}
            _installServer = new InstallServer();
            _server = new AdminServer();
            await _server.StartAsync(_installServer.Bootstrap, _installServer.Store, _installServer.Security);
            await _installServer.StartAsync();
            _auth=new AuthApiClient(_installServer.Bootstrap);
            ShowLogin();
        }
        catch (Exception ex)
        {
            AppDiagnostics.Record("SystemA", "STARTUP", ex);
            if (AppExitState.IsClosing) return;
            MessageBox.Show($"L&T Admin could not start.\n\n{ex.Message}", "L&T Admin", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private void ShowLogin()
    {
        var login=new LoginWindow(_auth!);
        if(login.ShowDialog()!=true || login.Session is null){Shutdown();return;}
        _session=login.Session with{RecoveryCode=""};
        _api=new AdminApiClient(_server!.Options,_installServer!.Bootstrap,_session.Token);
        _installApi=new InstallApiClient(_installServer.Bootstrap,_session.Token);
        var window=new MainWindow(_api,_server.Options,_installApi,_session.Identity,_session.ExpiresUtc);
        MainWindow=window;window.Show();
    }
    public async Task LogoutAsync()
    {
        if(_session is null || _logoutBusy)return;
        _logoutBusy=true;
        var session=_session;_session=null;
        try{await _auth!.LogoutAsync(session.Token);}catch(Exception ex){AppDiagnostics.Record("SystemA","LOGOUT",ex);}
        if(MainWindow is AutoShutdown.Admin.MainWindow window){window.StopForLogout();window.Close();}
        _api?.Dispose();_installApi?.Dispose();_api=null;_installApi=null;
        try{ShowLogin();}finally{_logoutBusy=false;}
    }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        AppExitState.BeginSessionEnding();
        if (MainWindow is AutoShutdown.Admin.MainWindow window) window.PrepareForExit();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppExitState.BeginClosing();
        _instance?.Dispose();
        AppDiagnostics.FinishExit("SystemA", async () =>
        {
            _auth?.Dispose();
            _api?.Dispose();
            _installApi?.Dispose();
            try { if (_server is not null) await _server.DisposeAsync(); }
            finally { if (_installServer is not null) await _installServer.DisposeAsync(); }
        });
        base.OnExit(e);
    }
}
