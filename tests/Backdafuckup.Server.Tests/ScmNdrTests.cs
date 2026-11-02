using Backdafuckup.Smb;

namespace Backdafuckup.Server.Tests;

/// <summary>NDR 2.0 encoding of the MS-SCMR calls, byte for byte (alignment, unique pointers, conformant varying strings).</summary>
public sealed class ScmNdrTests
{
    [Fact]
    public void ROpenSCManagerW_request()
    {
        var bytes = new NdrWriter().UniqueString(null).UniqueString("ServicesActive").UInt32(ScmClient.ScManagerAllAccess).ToArray();

        var expected = new List<byte>();
        expected.AddRange([0, 0, 0, 0]); // lpMachineName: null
        expected.AddRange([0x00, 0x00, 0x02, 0x00]); // lpDatabaseName: referent ID
        expected.AddRange([15, 0, 0, 0, 0, 0, 0, 0, 15, 0, 0, 0]); // max count, offset, actual count (with terminator)
        expected.AddRange(System.Text.Encoding.Unicode.GetBytes("ServicesActive\0")); // 30 bytes
        expected.AddRange([0, 0]); // pad to 4
        expected.AddRange([0x3F, 0x00, 0x0F, 0x00]); // SC_MANAGER_ALL_ACCESS
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void RStartServiceW_request_without_arguments()
    {
        var handle = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();

        var bytes = new NdrWriter().Handle(handle).UInt32(0).Null().ToArray();

        Assert.Equal([.. handle, 0, 0, 0, 0, 0, 0, 0, 0], bytes);
    }

    [Fact]
    public void Strings_of_odd_length_are_padded_before_the_next_value()
    {
        var bytes = new NdrWriter().String("ab").UInt32(7).ToArray();

        // 12 header bytes + "ab\0" (6 bytes) + 2 padding + 4
        Assert.Equal(24, bytes.Length);
        Assert.Equal([3, 0, 0, 0, 0, 0, 0, 0, 3, 0, 0, 0, (byte)'a', 0, (byte)'b', 0, 0, 0, 0, 0, 7, 0, 0, 0], bytes);
    }

    [Fact]
    public void RCreateServiceW_response_with_null_tag_id()
    {
        var handle = Enumerable.Range(100, 20).Select(i => (byte)i).ToArray();
        byte[] response = [0, 0, 0, 0, .. handle, 0x31, 0x04, 0, 0]; // tag id null, handle, ERROR_SERVICE_EXISTS (1073)

        var reader = new NdrReader(response);

        Assert.Null(reader.UniqueUInt32());
        Assert.Equal(handle, reader.Handle().Value);
        Assert.Equal(ScmException.ServiceExists, reader.UInt32());
    }

    [Theory]
    [InlineData(@"CORP\backup", "10.0.0.5", "CORP", "backup")]
    [InlineData(@".\Administrator", "10.0.0.5", "", "Administrator")]
    [InlineData("admin@corp.example", "10.0.0.5", "", "admin@corp.example")]
    [InlineData("admin", "10.0.0.5", "", "admin")]
    public void User_names_are_split_into_domain_and_user(string input, string address, string domain, string user) =>
        Assert.Equal((domain, user), SmbRemoteWindowsConnector.SplitUser(input, address));
}
