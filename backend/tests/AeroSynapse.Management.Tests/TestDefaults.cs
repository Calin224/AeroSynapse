global using static AeroSynapse.Management.Tests.TestDefaults;
using Xunit;

namespace AeroSynapse.Management.Tests;

internal static class TestDefaults
{
    public static CancellationToken Token => TestContext.Current.CancellationToken;
}
