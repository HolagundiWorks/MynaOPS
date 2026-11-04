using Office.Shared;
using Xunit;

namespace Office.Shared.Tests;

[Collection("environment")]
public class SuiteProjectLockTests : IDisposable
{
    public SuiteProjectLockTests() => Clear();

    public void Dispose() => Clear();

    private static void Clear()
    {
        Environment.SetEnvironmentVariable(SuiteWire.EnvProjectId, null);
        Environment.SetEnvironmentVariable(SuiteWire.EnvProjectOsp, null);
        Environment.SetEnvironmentVariable(SuiteWire.EnvProjectName, null);
        Environment.SetEnvironmentVariable(SuiteProjectLock.EnvStandalone, null);
    }

    [Fact]
    public void No_project_in_the_environment_is_no_lock()
    {
        Assert.Null(SuiteProjectLock.FromEnvironment());
    }

    [Fact]
    public void The_project_the_launcher_names_is_read_back()
    {
        Environment.SetEnvironmentVariable(SuiteWire.EnvProjectId, "17");
        Environment.SetEnvironmentVariable(SuiteWire.EnvProjectOsp, @"C:\Practice\p17\project.osp");
        Assert.Equal(new LockedProject(17, @"C:\Practice\p17\project.osp"), SuiteProjectLock.FromEnvironment());
    }

    [Fact]
    public void The_launcher_can_pass_the_studio_project_name()
    {
        Environment.SetEnvironmentVariable(SuiteWire.EnvProjectId, "17");
        Environment.SetEnvironmentVariable(SuiteWire.EnvProjectOsp, @"C:\Practice\p17\project.osp");
        Environment.SetEnvironmentVariable(SuiteWire.EnvProjectName, "Rao Residence");
        Assert.Equal(new LockedProject(17, @"C:\Practice\p17\project.osp", "Rao Residence"), SuiteProjectLock.FromEnvironment());
    }

    [Theory]
    [InlineData("", @"C:\p\project.osp")]
    [InlineData("abc", @"C:\p\project.osp")]
    [InlineData("17", "")]
    [InlineData("17", "   ")]
    public void A_half_or_damaged_pair_is_no_lock(string id, string osp)
    {
        Environment.SetEnvironmentVariable(SuiteWire.EnvProjectId, id);
        Environment.SetEnvironmentVariable(SuiteWire.EnvProjectOsp, osp);
        Assert.Null(SuiteProjectLock.FromEnvironment());
    }

    [Fact]
    public void Standalone_needs_the_switch_to_be_one()
    {
        Assert.False(SuiteProjectLock.Standalone);
        Environment.SetEnvironmentVariable(SuiteProjectLock.EnvStandalone, "1");
        Assert.True(SuiteProjectLock.Standalone);
        Environment.SetEnvironmentVariable(SuiteProjectLock.EnvStandalone, "true");
        Assert.False(SuiteProjectLock.Standalone);
    }
}
