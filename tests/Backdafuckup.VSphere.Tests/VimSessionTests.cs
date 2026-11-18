using System.Xml.Linq;
using Backdafuckup.Testing.Vim;
using Backdafuckup.VSphere.Vim;

namespace Backdafuckup.VSphere.Tests;

/// <summary>The vim25 client against the fake vCenter: login, properties with paging, tasks, faults and element order.</summary>
public sealed class VimSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<VimSession> ConnectAsync(FakeVcenter vcenter, string? password = null) =>
        VimSession.ConnectAsync(new VimTransport(new Uri("https://vcenter.corp.example/"), vcenter.CreateHandler()), vcenter.UserName, password ?? vcenter.Password, Ct);

    [Fact]
    public async Task Connecting_negotiates_the_version_reads_the_service_content_and_logs_in()
    {
        var vcenter = new FakeVcenter();
        await using (var session = await ConnectAsync(vcenter))
        {
            Assert.Equal("7.0.3.0", session.Transport.Version);
            Assert.Equal(("VirtualCenter", "7.0.3.0", "1c0306a4-870a-4572-9472-3fbdd6561a0a"), (session.Content.About.ApiType, session.Content.About.ApiVersion, session.Content.About.InstanceUuid));
            Assert.Equal(new ManagedObjectReference("Folder", "group-d1"), session.Content.RootFolder);
            Assert.True(session.Transport.HasSession);
            Assert.Equal("vcenter.corp.example", session.Endpoint);
        }

        Assert.Equal(["RetrieveServiceContent", "Login", "Logout"], vcenter.Calls);
    }

    [Fact]
    public async Task A_wrong_password_names_the_user_and_the_sso_domain()
    {
        var vcenter = new FakeVcenter();
        var ex = await Assert.ThrowsAsync<VimFaultException>(() => ConnectAsync(vcenter, "wrong"));
        Assert.Equal("InvalidLogin", ex.FaultType);
        Assert.Equal(
            "vCenter vcenter.corp.example: Login failed: the user name or password of 'backup@vsphere.local' is wrong (a local SSO user needs its domain, e.g. user@vsphere.local) (InvalidLogin)",
            ex.Message);
    }

    [Fact]
    public async Task All_objects_of_a_type_come_back_over_several_pages_and_the_view_is_destroyed()
    {
        var vcenter = new FakeVcenter { PageSize = 2 };
        for (var i = 1; i <= 5; i++)
        {
            vcenter.Add("VirtualMachine", $"vm-{i}", ("name", Val.Str($"app{i:00}")), ("runtime.powerState", Val.Str("poweredOn")));
        }

        vcenter.Add("HostSystem", "host-1", ("name", Val.Str("esx01.corp.example")));
        await using var session = await ConnectAsync(vcenter);

        var vms = await session.RetrieveAllAsync("VirtualMachine", ["name", "runtime.powerState", "config.uuid"], Ct);

        Assert.Equal(["app01", "app02", "app03", "app04", "app05"], vms.Select(v => v.Str("name")));
        Assert.All(vms, v => Assert.Equal("poweredOn", v.Str("runtime.powerState")));
        Assert.All(vms, v => Assert.Null(v["config.uuid"]));
        Assert.Equal(2, vcenter.Calls.Count(c => c == "ContinueRetrievePropertiesEx"));
        Assert.Contains("DestroyView", vcenter.Calls);
        Assert.Empty(vcenter.Objects("ContainerView"));

        // The request as vCenter expects it: elements in XSD order, the traversal with its xsi:type.
        var request = vcenter.Requests.First(r => r.Name.LocalName == "RetrievePropertiesEx");
        Assert.Equal(
            "<RetrievePropertiesEx xmlns=\"urn:vim25\"><_this type=\"PropertyCollector\">propertyCollector</_this><specSet><propSet><type>VirtualMachine</type>" +
            "<pathSet>name</pathSet><pathSet>runtime.powerState</pathSet><pathSet>config.uuid</pathSet></propSet><objectSet><obj type=\"ContainerView\">" +
            vcenter.Requests.First(r => r.Name.LocalName == "DestroyView").Element(FakeVcenter.Ns + "_this")!.Value +
            "</obj><skip>true</skip><selectSet xsi:type=\"TraversalSpec\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><name>view</name><type>ContainerView</type><path>view</path>" +
            "<skip>false</skip></selectSet></objectSet></specSet><options><maxObjects>500</maxObjects></options></RetrievePropertiesEx>",
            System.Text.RegularExpressions.Regex.Replace(request.ToString(SaveOptions.DisableFormatting), @"\bp\d+(?=[:=])", "xsi"));
    }

    [Fact]
    public async Task A_dropped_session_is_logged_in_again_once()
    {
        var vcenter = new FakeVcenter();
        vcenter.Add("HostSystem", "host-1", ("name", Val.Str("esx01.corp.example")));
        await using var session = await ConnectAsync(vcenter);

        vcenter.ExpireSessions();
        var hosts = await session.RetrievePropertiesAsync([new ManagedObjectReference("HostSystem", "host-1")], ["name"], Ct);

        Assert.Equal("esx01.corp.example", Assert.Single(hosts).Str("name"));
        Assert.Equal(2, vcenter.Calls.Count(c => c == "Login"));
    }

    [Fact]
    public async Task A_task_is_waited_for_and_a_failed_one_names_the_object_and_vcenters_message()
    {
        var vcenter = new FakeVcenter { TaskPolls = 2 };
        var vm = vcenter.Add("VirtualMachine", "vm-7", ("name", Val.Str("app07")));
        vcenter.On("CreateSnapshot_Task", request => vcenter.StartTask(vm, "VirtualMachine.createSnapshot",
            () => request.Element(FakeVcenter.Ns + "memory")!.Value == "true"
                ? throw new VimFault("InvalidState", "The operation is not allowed in the current state.")
                : Val.Mor("val", vcenter.Add("VirtualMachineSnapshot", "snapshot-1"))));
        await using var session = await ConnectAsync(vcenter);

        var task = await session.StartTaskAsync("CreateSnapshot_Task", new ManagedObjectReference("VirtualMachine", "vm-7"), Ct,
            VimXml.E("name", "backdafuckup temporary snapshot"), VimXml.E("description", "test"), VimXml.E("memory", "false"), VimXml.E("quiesce", "false"));
        var progress = new List<int>();
        var result = await session.WaitForTaskAsync(task, Ct, new SynchronousProgress(progress.Add));
        Assert.Equal(new ManagedObjectReference("VirtualMachineSnapshot", "snapshot-1"), result!.AsRef());
        Assert.Equal(100, progress[^1]);

        var failing = await session.StartTaskAsync("CreateSnapshot_Task", new ManagedObjectReference("VirtualMachine", "vm-7"), Ct,
            VimXml.E("name", "x"), VimXml.E("memory", "true"), VimXml.E("quiesce", "false"));
        var ex = await Assert.ThrowsAsync<VimTaskException>(() => session.WaitForTaskAsync(failing, Ct));
        Assert.Equal("VirtualMachine.createSnapshot on app07 failed: The operation is not allowed in the current state. (InvalidState)", ex.Message);
    }

    [Fact]
    public async Task Elements_out_of_xsd_order_are_refused_like_vcenter_does()
    {
        var vcenter = new FakeVcenter();
        await using var session = await ConnectAsync(vcenter);

        // quiesce before memory: CreateSnapshotRequestType is name, description, memory, quiesce.
        var ex = await Assert.ThrowsAsync<VimFaultException>(() => session.InvokeAsync("CreateSnapshot_Task", new ManagedObjectReference("VirtualMachine", "vm-1"), Ct,
            VimXml.E("name", "x"), VimXml.E("quiesce", "false"), VimXml.E("memory", "false")));
        Assert.Equal("InvalidRequest", ex.FaultType);
        Assert.Contains("Unexpected element tag \"memory\" seen while parsing serialized DataObject of type CreateSnapshotRequestType", ex.Message, StringComparison.Ordinal);

        // A nested data object is checked by its xsi:type: VirtualDisk lists backing before controllerKey.
        var reconfigure = await Assert.ThrowsAsync<VimFaultException>(() => session.InvokeAsync("ReconfigVM_Task", new ManagedObjectReference("VirtualMachine", "vm-1"), Ct,
            VimXml.E("spec", VimXml.E("deviceChange", VimXml.E("operation", "add"),
                VimXml.Typed("device", "VirtualDisk", VimXml.E("key", -1), VimXml.E("controllerKey", 1001), VimXml.Typed("backing", "VirtualDiskFlatVer2BackingInfo", VimXml.E("fileName", "[ds] a/a.vmdk")))))));
        Assert.Contains("Unexpected element tag \"backing\" seen while parsing serialized DataObject of type VirtualDisk", reconfigure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Byte_arrays_are_one_signed_byte_element_per_byte()
    {
        var value = XElement.Parse("<val xmlns=\"urn:vim25\"><byte>45</byte><byte>-128</byte><byte>-1</byte><byte>0</byte></val>");
        Assert.Equal(new byte[] { 45, 0x80, 0xFF, 0 }, new VimObject(value).Bytes());
    }

    private sealed class SynchronousProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
