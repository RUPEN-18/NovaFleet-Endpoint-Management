using AutoShutdown.Shared;
using System.IO;
namespace AutoShutdown.Admin.Infrastructure;
public sealed partial class InstallStore
{
    public async Task<(int Active,int Success,int Failed)> CountsAsync(AdminIdentity actor)
    {await _gate.WaitAsync();try{var jobs=_data.Jobs.Where(x=>actor.CanDevice(x.DeviceId)).ToList();return(jobs.Count(x=>!SevenZipPolicy.IsTerminal(x.State)),jobs.Count(x=>x.State is InstallState.Installed or InstallState.AlreadyInstalled),jobs.Count(x=>x.State==InstallState.Failed));}finally{_gate.Release();}}
    public async Task<List<EnrolledDevice>> DevicesAsync()
    {await _gate.WaitAsync();try{return _data.Devices.Where(x=>!x.Removed).ToList();}finally{_gate.Release();}}
    public async Task<List<ApprovedPackage>> PackagesAsync()
    {await _gate.WaitAsync();try{return _data.Packages.Concat(_data.Package is null?Array.Empty<ApprovedPackage>():new[]{_data.Package}).Where(ApplicationPackagePolicy.IsGeneral).DistinctBy(x=>x.Id).ToList();}finally{_gate.Release();}}
}
