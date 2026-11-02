using Backdafuckup.FileLevel;

namespace Backdafuckup.FileLevel.Tests;

[Trait("Category", "Unit")]
public sealed class GuestPathTests
{
    [Theory]
    [InlineData("..")]
    [InlineData("a/../b")]
    [InlineData("./a")]
    [InlineData("a/\0")]
    public void Traversal_and_invalid_names_are_rejected(string path) => Assert.Throws<ArgumentException>(() => GuestPath.Parse(path));

    [Fact]
    public void Slashes_are_normalized_and_backslash_is_a_normal_character()
    {
        Assert.Equal(["a", "b"], GuestPath.Parse("//a//b/").Components);
        Assert.Equal(["a\\b"], GuestPath.Parse("a\\b").Components);
        Assert.True(GuestPath.Parse(string.Empty).IsRoot);
        Assert.True(GuestPath.Parse("a").Contains(GuestPath.Parse("a/b")));
        Assert.False(GuestPath.Parse("a").Contains(GuestPath.Parse("ab")));
    }
}
