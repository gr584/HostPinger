using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HostPinger.Core.Services
{
    public sealed class PingSender : IPingSender
    {
        public async Task<int?> SendPingAsync(string address, int timeoutMilliseconds, CancellationToken cancellationToken = default)
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(
                    address,
                    TimeSpan.FromMilliseconds(timeoutMilliseconds),
                    cancellationToken: cancellationToken);
                return reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : null;
            }
            catch (PingException)
            {
                return null;
            }
            catch (SocketException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
