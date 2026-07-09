namespace PulseTerm.Core.Models;

public class SessionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public string Name { get; set; } = string.Empty;
    
    public string Host { get; set; } = string.Empty;
    
    public int Port { get; set; } = 22;
    
    public string Username { get; set; } = string.Empty;
    
    public AuthMethod AuthMethod { get; set; } = AuthMethod.Password;
    
    public string? Password { get; set; }

    /// <summary>是否记住密码(AES-256 加密落盘);为 false 时密码仅用于本次连接,不持久化。</summary>
    public bool RememberPassword { get; set; } = true;

    public string? PrivateKeyPath { get; set; }
    
    public string? PrivateKeyPassphrase { get; set; }
    
    public Guid? GroupId { get; set; }

    public DateTime? LastConnectedAt { get; set; }

    public List<string> Tags { get; set; } = new();

    /// <summary>跳板主机(ProxyJump,§12 P1-2):引用另一条已保存配置作为堡垒机;
    /// 跳板配置自身还可以再配跳板,链式即多段跳。null = 直连。</summary>
    public Guid? JumpHostProfileId { get; set; }
}
