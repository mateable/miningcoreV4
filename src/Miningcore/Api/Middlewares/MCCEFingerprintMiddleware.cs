using Microsoft.AspNetCore.Http;

namespace Miningcore.Api.Middlewares;

public class MCCEFingerprintMiddleware
{
    public MCCEFingerprintMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    private readonly RequestDelegate _next;

    public async Task Invoke(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Pool-Generator"] = "mcce";
            context.Response.Headers["Server"] = "miningcore";
            return Task.CompletedTask;
        });

        await _next(context);
    }
}
