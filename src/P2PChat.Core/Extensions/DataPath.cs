namespace P2PChat.Core.Extensions;

/// <summary>
/// 数据目录解析 —— 所有运行时文件(身份密钥/会话密钥/群密钥/联系人/已知节点/日志)统一存放在
/// <b>用户主目录下的 <c>.p2pc</c> 文件夹</b>中，与程序安装位置解耦。
/// </summary>
/// <remarks>
/// <para>
/// 默认位置：<c>%USERPROFILE%\.p2pc</c>（Windows）/ <c>$HOME/.p2pc</c>（Unix）。
/// 这样做的原因：
/// </para>
/// <list type="bullet">
///   <item>程序可能放在只读目录或需要管理员权限的路径下，写入程序目录会失败；</item>
///   <item>重新发布/覆盖 exe 不会连带删除用户身份密钥与聊天数据；</item>
///   <item>同一用户的多份程序副本天然共享同一身份，符合"一个用户一个身份"的语义。</item>
/// </list>
/// <para>
/// 多实例共存（如端到端测试同时拉起两个节点）时，可用环境变量 <c>P2PCHAT_DATA_DIR</c>
/// 显式覆盖该目录，从而让每个实例拥有独立的身份与密钥。
/// 也可调用 <see cref="SetRoot"/> 在代码中指定（测试用）。
/// </para>
/// </remarks>
public static class DataPath
{
    /// <summary>数据文件夹名称（位于用户主目录下）。</summary>
    public const string FolderName = ".p2pc";

    /// <summary>覆盖数据目录的环境变量名（用于多实例隔离）。</summary>
    public const string OverrideEnvironmentVariable = "P2PCHAT_DATA_DIR";

    private static string? _root;
    private static readonly object _initLock = new();

    /// <summary>
    /// 获取数据根目录 —— 默认为 <c>用户主目录/.p2pc</c>，若设置了
    /// <see cref="OverrideEnvironmentVariable"/> 则使用其值。
    /// 目录不存在时会被自动创建。
    /// </summary>
    public static string Root
    {
        get
        {
            if (_root != null) return _root;

            lock (_initLock)
            {
                if (_root != null) return _root;

                var overrideDir = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
                _root = string.IsNullOrWhiteSpace(overrideDir)
                    ? Path.Combine(ResolveHomeDirectory(), FolderName)
                    : Path.GetFullPath(overrideDir);

                Directory.CreateDirectory(_root);
                return _root;
            }
        }
    }

    /// <summary>
    /// 解析用户主目录。优先 <c>USERPROFILE</c>（Windows），其次 <c>HOME</c>（Unix），
    /// 最后回退到 .NET 的本地应用数据目录，保证任何环境下都有可写位置。
    /// </summary>
    private static string ResolveHomeDirectory()
    {
        var home = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetEnvironmentVariable("HOME");

        if (string.IsNullOrWhiteSpace(home))
        {
            // 兜底：Windows 下为 %LOCALAPPDATA%，Unix 下为 ~/.config
            home = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify);
        }

        if (string.IsNullOrWhiteSpace(home))
            throw new InvalidOperationException(
                "无法解析用户主目录(USERPROFILE / HOME 均为空)，请设置 P2PCHAT_DATA_DIR 指定数据目录。");

        return home;
    }

    /// <summary>
    /// 手动设置数据根目录 (用于测试或自定义路径)。设置后 <see cref="Root"/> 不再读取环境变量。
    /// </summary>
    public static void SetRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_initLock)
        {
            _root = Path.GetFullPath(path);
            Directory.CreateDirectory(_root);
        }
    }

    /// <summary>重置缓存，使下次访问 <see cref="Root"/> 时重新解析（测试用）。</summary>
    public static void Reset()
    {
        lock (_initLock)
        {
            _root = null;
        }
    }

    /// <summary>
    /// 获取数据文件的完整路径。
    /// </summary>
    public static string GetPath(string filename) => Path.Combine(Root, filename);

    /// <summary>
    /// 获取（并按需创建）数据根目录下的子目录完整路径，例如日志目录 <c>logs</c>。
    /// </summary>
    public static string GetDirectory(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
