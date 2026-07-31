using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Stepwright.Web;

/// <summary>
/// The small door a sign in comes back through.
///
/// Several services finish a sign in by sending the browser to an address on this machine. That
/// address has to be listening when the browser arrives, and it has to be on this machine and
/// nowhere else, which is the whole point: the answer is handed to the process that asked for it
/// rather than to a server somewhere that could be anybody.
///
/// It listens on the loopback address only, so nothing outside this machine can reach it, and it
/// closes the moment it has its answer.
/// </summary>
public sealed class Loopback : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _path;

    /// <summary>
    /// Opens the door. A port of zero means any free one, which is what a well behaved
    /// application does. Some services insist on a particular port because that is what they
    /// registered, and those pass it in.
    /// </summary>
    public Loopback(int port = 0, string path = "/callback")
    {
        _path = path.StartsWith('/') ? path : "/" + path;

        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    /// <summary>The address to hand the service, which is where it will send the browser back.</summary>
    public string Address => $"http://localhost:{Port}{_path}";

    /// <summary>
    /// Waits for the browser to arrive and gives back what it brought. Anything that is not the
    /// address being waited for, and a browser asks for several, is answered politely and
    /// ignored.
    /// </summary>
    public async Task<NameValueCollection> WaitAsync(
        string? state,
        string finished,
        CancellationToken token,
        TimeSpan? limit = null)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(limit ?? TimeSpan.FromMinutes(5));

        while (true)
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
            using NetworkStream stream = client.GetStream();

            var buffer = new byte[8192];
            int read = await stream.ReadAsync(buffer, stop.Token).ConfigureAwait(false);
            string head = Encoding.UTF8.GetString(buffer, 0, read);

            int start = head.IndexOf(' ') + 1;
            int end = head.IndexOf(' ', Math.Max(start, 1));

            if (start <= 0 || end <= start)
            {
                await ReplyAsync(stream, "Stepwright could not read that.", stop.Token).ConfigureAwait(false);
                continue;
            }

            string target = head[start..end];

            if (!target.StartsWith(_path, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(stream, "Nothing to see here.", stop.Token).ConfigureAwait(false);
                continue;
            }

            NameValueCollection query =
                System.Web.HttpUtility.ParseQueryString(new Uri("http://localhost" + target).Query);

            string? refused = query["error_description"] ?? query["error"];

            if (!string.IsNullOrEmpty(refused))
            {
                await ReplyAsync(stream, "That sign in was refused. You can close this tab.", stop.Token)
                    .ConfigureAwait(false);

                throw new InvalidOperationException("The sign in was refused. " + refused);
            }

            // The state proves this answer belongs to the request this app made, rather than to
            // one somebody else started in the same browser.
            if (state is not null && !string.Equals(query["state"], state, StringComparison.Ordinal))
            {
                await ReplyAsync(stream, "That answer was not the one asked for.", stop.Token).ConfigureAwait(false);
                throw new InvalidOperationException("The answer from the browser did not match the request.");
            }

            await ReplyAsync(stream, finished, stop.Token).ConfigureAwait(false);
            return query;
        }
    }

    private static async Task ReplyAsync(NetworkStream stream, string message, CancellationToken token)
    {
        string page =
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Stepwright</title></head>"
            + "<body style=\"font-family:Segoe UI,Helvetica,Arial,sans-serif;padding:48px;\">"
            + "<h2>Stepwright</h2><p>" + WebUtility.HtmlEncode(message) + "</p></body></html>";

        byte[] body = Encoding.UTF8.GetBytes(page);

        byte[] headers = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n"
            + "Content-Type: text/html; charset=utf-8\r\n"
            + "Cache-Control: no-store\r\n"
            + "Connection: close\r\n"
            + "Content-Length: " + body.Length + "\r\n\r\n");

        await stream.WriteAsync(headers, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // A listener that has already given up its port needs nothing further.
        }
    }
}
