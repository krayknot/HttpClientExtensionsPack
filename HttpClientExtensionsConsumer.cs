// HttpClientExtensionsConsumer.cs
// Example consumer demonstrating usage of HttpClientExtensions
// Target: .NET 8.0+

using System;
using System.Net.Http;
using System.Threading.Tasks;
using HttpClientExtensionsPack;

namespace DemoConsumer
{
    public static class Program
    {
        public static async Task Main()
        {
            var client = HttpClientExtensions.CreateDefaultClient("https://jsonplaceholder.typicode.com");

            // GET with JSON
            var post = await client.GetFromJsonSafeAsync<object>("/posts/1");
            Console.WriteLine(post);

            // POST with JSON
            var newPost = new
            {
                title = "foo",
                body = "bar",
                userId = 1
            };
            var created = await client.PostAsJsonSafeAsync<object, object>("/posts", newPost);
            Console.WriteLine(created);

            // File download
            await client.DownloadToFileAsync("/photos/1", "sample.json");
            Console.WriteLine("Downloaded sample.json");

            // Simple HEAD request
            var head = await client.HeadAsync("/posts/1");
            Console.WriteLine($"HEAD status: {head.StatusCode}");

            // Timeout
            try
            {
                await client.GetFromJsonSafeAsync<object>("/posts/1").WithTimeout(TimeSpan.FromSeconds(1));
                Console.WriteLine("Request within timeout");
            }
            catch (TimeoutException)
            {
                Console.WriteLine("Timeout occurred");
            }
        }
    }
}
