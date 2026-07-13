namespace VelaShell.Presentation.Services;

/// <summary>连接测试的结果:是否成功,以及失败时的错误信息。</summary>
/// <param name="Success">连接测试是否成功。</param>
/// <param name="ErrorMessage">测试失败时的错误描述;成功时为 <see langword="null" />。</param>
public sealed record ConnectionTestResult(bool Success, string? ErrorMessage = null);
