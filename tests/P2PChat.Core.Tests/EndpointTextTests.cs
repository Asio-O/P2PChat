using P2PChat.Core.Extensions;
using Shouldly;

namespace P2PChat.Core.Tests;

/// <summary>
/// <c>ip:port</c> 文本解析 —— 手工添加联系人时的显式端点（<c>/add &lt;节点ID&gt; &lt;ip:port&gt;</c>）。
/// </summary>
public class EndpointTextTests
{
    [Theory]
    [InlineData("192.168.1.5:5000", true)]
    [InlineData("127.0.0.1:20081", true)]
    [InlineData("0.0.0.0:1", true)]
    [InlineData("10.0.0.1:65535", true)]
    [InlineData(" 192.168.1.5:5000 ", true)]     // 两端空白应被忽略
    [InlineData("[::1]:5000", true)]             // IPv6 字面量
    [InlineData("[fe80::1]:1234", true)]
    [InlineData("192.168.1.5", false)]           // 缺端口
    [InlineData("192.168.1.5:", false)]          // 端口为空
    [InlineData(":5000", false)]                 // 缺主机
    [InlineData("192.168.1.5:0", false)]         // 端口 0 不合法
    [InlineData("192.168.1.5:65536", false)]     // 端口越界
    [InlineData("192.168.1.5:abc", false)]
    [InlineData("example.com:5000", false)]      // 只接受字面 IP，不做 DNS 解析
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void 端点解析_只接受字面IP与合法端口(string? text, bool expected)
    {
        EndpointText.TryParse(text, out _).ShouldBe(expected);
    }

    [Fact]
    public void 端点解析_成功后地址与端口正确()
    {
        EndpointText.TryParse("192.168.1.5:20081", out var endPoint).ShouldBeTrue();

        endPoint.Address.ToString().ShouldBe("192.168.1.5");
        endPoint.Port.ShouldBe(20081);
    }

    [Fact]
    public void 端点格式化_与解析互逆()
    {
        EndpointText.TryParse("10.1.2.3:4567", out var endPoint).ShouldBeTrue();
        EndpointText.Format(endPoint).ShouldBe("10.1.2.3:4567");
    }
}
