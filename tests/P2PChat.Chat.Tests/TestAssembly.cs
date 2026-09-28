// 本程序集中的 ContactService 相关用例会切换进程级静态状态 DataPath.Root，
// 必须串行执行，否则并发用例之间会互相踩数据目录。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
