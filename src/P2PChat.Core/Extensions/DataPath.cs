namespace P2PChat.Core.Extensions;

/// <summary>
/// 数据目录路径 — 所有持久化文件存储在程序目录下的 data/ 中
/// </summary>
public static class DataPath
{
    private static string? _root;

    /// <summary>
    /// 获取数据根目录 (程序目录/data/)
    /// </summary>
    public static string Root
    {
        get
        {
            if (_root == null)
            {
                // 允许用环境变量覆盖数据目录：多个实例可共用同一个 exe 路径而各自隔离数据。
                // 也避免为隔离数据而复制 exe —— 复制会导致 Windows 防火墙对每个新程序路径重复询问。
                var overrideDir = Environment.GetEnvironmentVariable("P2PCHAT_DATA_DIR");
                _root = string.IsNullOrWhiteSpace(overrideDir)
                    ? Path.Combine(AppContext.BaseDirectory, "data")
                    : Path.GetFullPath(overrideDir);
                Directory.CreateDirectory(_root);
            }
            return _root;
        }
    }

    /// <summary>
    /// 手动设置数据根目录 (用于测试或自定义路径)
    /// </summary>
    public static void SetRoot(string path)
    {
        _root = path;
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// 获取数据文件完整路径
    /// </summary>
    public static string GetPath(string filename) => Path.Combine(Root, filename);
}
