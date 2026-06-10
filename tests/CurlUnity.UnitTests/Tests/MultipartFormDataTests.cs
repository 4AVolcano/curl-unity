using System;
using System.IO;
using CurlUnity.Http;
using Xunit;

namespace CurlUnity.UnitTests.Tests
{
    public class MultipartFormDataTests
    {
        [Fact]
        public void BuildStream_WithStreamParts_SecondCallThrows()
        {
            var form = new MultipartFormData();
            var src = new MemoryStream(new byte[] { 1, 2, 3 });
            form.AddFile("f", "a.bin", src, 3);

            using var first = form.BuildStream();

            // 两次 BuildStream 返回的 stream 共享同一个源 Stream，第一次读完后
            // 第二次必然中途提前 EOF——必须在入口 fail-fast 而不是发送时才爆
            var ex = Assert.Throws<InvalidOperationException>(() => form.BuildStream());
            Assert.Contains("只能 BuildStream 一次", ex.Message);
        }

        [Fact]
        public void BuildStream_WithoutStreamParts_IsRepeatable()
        {
            var form = new MultipartFormData();
            form.AddText("k", "v");

            // 纯内存 part 可重复构建（byte[] 可重复读），两次产出一致
            using var s1 = form.BuildStream();
            var b1 = ReadAll(s1);
            using var s2 = form.BuildStream();
            var b2 = ReadAll(s2);
            Assert.Equal(b1, b2);
            Assert.Equal(form.ContentLength, b1.LongLength);
        }

        [Fact]
        public void Build_IsRepeatable()
        {
            var form = new MultipartFormData();
            form.AddText("k", "v");
            Assert.Equal(form.Build(), form.Build());
        }

        private static byte[] ReadAll(Stream s)
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
