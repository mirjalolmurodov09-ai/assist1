using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using ClassroomControl.StudentAgent.Communication.Protocol;
using ClassroomControl.StudentAgent.Communication.Security;
using ClassroomControl.StudentAgent.Communication.Tcp;
using ClassroomControl.StudentAgent.Infrastructure.Helpers;
using ClassroomControl.StudentAgent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassroomControl.StudentAgent.Services;

/// <summary>An established TCP+TLS connection that is not yet authenticated.</summary>
public sealed class TeacherConnection : IAsyncDisposable
{
    private readonly TcpClient _tcp;

    public TeacherConnection(TcpClient tcp, IFramedChannel channel, string certificateFingerprint)
    {
        _tcp = tcp;
        Channel = channel;
        CertificateFingerprint = certificateFingerprint;
    }

    public IFramedChannel Channel { get; }
    public string CertificateFingerprint { get; }

    public async ValueTask DisposeAsync()
    {
        await Channel.DisposeAsync().ConfigureAwait(false);
        _tcp.Dispose();
    }
}

public interface IConnectionService
{
    Task<TeacherConnection> ConnectAsync(ConnectionInfo teacher, StudentSettings settings, CancellationToken cancellationToken);
}

/// <summary>Opens TCP and performs the TLS handshake. The Teacher uses a self-signed certificate, so trust is established by
/// (1) pinning its fingerprint after the first connection and (2) the channel-bound HMAC authentication that follows.</summary>
public sealed class ConnectionService : IConnectionService
{
    private readonly AgentOptions _options;
    private readonly ILogger<ConnectionService> _logger;

    public ConnectionService(IOptions<AgentOptions> options, ILogger<ConnectionService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<TeacherConnection> ConnectAsync(ConnectionInfo teacher, StudentSettings settings, CancellationToken cancellationToken)
    {
        if (!AddressPolicy.IsPermitted(teacher.Address, _options.AllowLoopbackTeacher))
            throw new ProtocolException(ErrorCodes.UntrustedNetwork, "Teacher address is not on a local network.");

        _logger.LogInformation("Connection Started to {Address}:{Port}", teacher.Address, teacher.Port);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.ConnectionTimeoutSeconds));

        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            await tcp.ConnectAsync(teacher.Address, teacher.Port, timeout.Token).ConfigureAwait(false);

            var pinned = settings.PinnedTeacherCertificate;
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, certificate, _, _) =>
                certificate is not null && (string.IsNullOrEmpty(pinned)
                    || string.Equals(pinned, CertificateFingerprint.Of(certificate), StringComparison.OrdinalIgnoreCase)));
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = ProtocolConstants.TlsTargetName,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                }, timeout.Token).ConfigureAwait(false);
            }
            catch (AuthenticationException ex)
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                _logger.LogError("Security Error: TLS handshake with the Teacher was rejected ({Reason}).", ex.Message);
                throw new ProtocolException(ErrorCodes.UntrustedCertificate,
                    "Teacher sertifikati ishonchli emas yoki o'zgargan. Settings → Security bo'limida sertifikatni qayta o'rnating.", ex);
            }

            var remote = ssl.RemoteCertificate ?? throw new ProtocolException(ErrorCodes.UntrustedCertificate, "Teacher certificate is missing.");
            return new TeacherConnection(tcp, new FramedConnection(ssl, _options.MaxFrameBytes), CertificateFingerprint.Of(remote));
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }
}
