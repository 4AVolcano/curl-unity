using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CurlUnity.Http
{
    public static class HttpClientExtensions
    {
        public static Task<IHttpResponse> GetAsync(this IHttpClient client, string url,
            CancellationToken ct = default)
        {
            var req = new HttpRequest { Method = HttpMethod.Get, Url = url };
            return client.SendAsync(req, ct);
        }

        public static Task<IHttpResponse> PostAsync(this IHttpClient client, string url,
            byte[] body, string contentType = "application/octet-stream",
            CancellationToken ct = default)
        {
            var req = new HttpRequest
            {
                Method = HttpMethod.Post,
                Url = url,
                Body = body,
                Headers = new[] { new KeyValuePair<string, string>("Content-Type", contentType) }
            };
            return client.SendAsync(req, ct);
        }

        public static Task<IHttpResponse> PostJsonAsync(this IHttpClient client, string url,
            string json, CancellationToken ct = default)
        {
            return client.PostAsync(url, Encoding.UTF8.GetBytes(json), "application/json", ct);
        }

        public static Task<IHttpResponse> PutAsync(this IHttpClient client, string url,
            byte[] body, string contentType = "application/octet-stream",
            CancellationToken ct = default)
        {
            var req = new HttpRequest
            {
                Method = HttpMethod.Put,
                Url = url,
                Body = body,
                Headers = new[] { new KeyValuePair<string, string>("Content-Type", contentType) }
            };
            return client.SendAsync(req, ct);
        }

        public static Task<IHttpResponse> DeleteAsync(this IHttpClient client, string url,
            CancellationToken ct = default)
        {
            var req = new HttpRequest { Method = HttpMethod.Delete, Url = url };
            return client.SendAsync(req, ct);
        }

        /// <summary>POST multipart/form-data 表单。</summary>
        public static Task<IHttpResponse> PostMultipartAsync(this IHttpClient client, string url,
            MultipartFormData form, CancellationToken ct = default)
        {
            if (form == null) throw new ArgumentNullException(nameof(form));
            var req = new HttpRequest
            {
                Method = HttpMethod.Post,
                Url = url,
                Body = form.Build(),
                Headers = new[] { new KeyValuePair<string, string>("Content-Type", form.ContentType) }
            };
            return client.SendAsync(req, ct);
        }
    }
}
