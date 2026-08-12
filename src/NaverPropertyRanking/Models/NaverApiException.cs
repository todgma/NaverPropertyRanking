using System.Net;

namespace NaverPropertyRanking.Models;

public sealed class NaverApiException : Exception
{
    public NaverApiException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner) => StatusCode = statusCode;

    public HttpStatusCode? StatusCode { get; }
}
