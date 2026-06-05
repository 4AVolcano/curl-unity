using System;
using System.Collections.Generic;
using System.Text;
using CurlUnity.Sse;
using Xunit;

namespace CurlUnity.UnitTests.Tests
{
    /// <summary>
    /// <see cref="SseEventParser"/> 协议解析单测。参照 WHATWG HTML "parse an event stream"。
    /// 重点覆盖跨 chunk 边界（UTF-8 多字节、CRLF、BOM）——这是流式解析最易出错处。
    /// </summary>
    public class SseEventParserTests
    {
        /// <summary>按 UTF-8 把若干字符串 chunk 依次喂给同一个 parser，收集产出的事件。</summary>
        private static List<SseEvent> Parse(params string[] chunks)
        {
            var parser = new SseEventParser();
            var events = new List<SseEvent>();
            foreach (var c in chunks)
            {
                var bytes = Encoding.UTF8.GetBytes(c);
                parser.Feed(bytes, 0, bytes.Length, events.Add);
            }
            return events;
        }

        /// <summary>按原始字节 chunk 依次喂入（用于 BOM / UTF-8 切分等字节级用例）。</summary>
        private static List<SseEvent> ParseBytes(params byte[][] chunks)
        {
            var parser = new SseEventParser();
            var events = new List<SseEvent>();
            foreach (var c in chunks)
                parser.Feed(c, 0, c.Length, events.Add);
            return events;
        }

        [Fact]
        public void SingleDataLine_ProducesMessageEvent()
        {
            var events = Parse("data: hello\n\n");
            var e = Assert.Single(events);
            Assert.Equal("message", e.EventType);
            Assert.Equal("hello", e.Data);
            Assert.Equal("", e.LastEventId);
        }

        [Fact]
        public void MultipleDataLines_JoinedWithNewline()
        {
            var events = Parse("data: a\ndata: b\n\n");
            Assert.Equal("a\nb", Assert.Single(events).Data);
        }

        [Fact]
        public void LeadingSpaceAfterColon_StrippedOnce()
        {
            // "data:hello" 与 "data: hello" 等价；"data:  x"（两空格）只去一个 → " x"
            Assert.Equal("hello", Assert.Single(Parse("data:hello\n\n")).Data);
            Assert.Equal("hello", Assert.Single(Parse("data: hello\n\n")).Data);
            Assert.Equal(" x", Assert.Single(Parse("data:  x\n\n")).Data);
        }

        [Fact]
        public void EventField_SetsEventType()
        {
            var e = Assert.Single(Parse("event: ping\ndata: x\n\n"));
            Assert.Equal("ping", e.EventType);
            Assert.Equal("x", e.Data);
        }

        [Fact]
        public void BlankLines_WithoutDataField_DoNotDispatch()
        {
            Assert.Empty(Parse("\n\n"));
        }

        [Fact]
        public void EventFieldWithoutData_DoesNotDispatch_AndClearsType()
        {
            // 出现 event 但无 data → 不 dispatch；且 event buffer 被清，不污染下一个事件
            var events = Parse("event: ping\n\ndata: x\n\n");
            var e = Assert.Single(events);
            Assert.Equal("message", e.EventType);
            Assert.Equal("x", e.Data);
        }

        [Fact]
        public void EmptyDataValue_DoesDispatch()
        {
            // "data:" 空值 —— data 字段出现过 → dispatch，Data=""
            var e = Assert.Single(Parse("data:\n\n"));
            Assert.Equal("", e.Data);
        }

        [Fact]
        public void NoColonLine_TreatedAsFieldWithEmptyValue()
        {
            // "data"（无冒号）→ field="data", value="" → Data=""
            var e = Assert.Single(Parse("data\n\n"));
            Assert.Equal("", e.Data);
        }

        [Fact]
        public void IdField_RetainedAcrossEvents()
        {
            var events = Parse("id: 42\ndata: x\n\ndata: y\n\n");
            Assert.Equal(2, events.Count);
            Assert.Equal("42", events[0].LastEventId);
            Assert.Equal("42", events[1].LastEventId); // id 跨事件保留
        }

        [Fact]
        public void IdField_ContainingNul_Ignored()
        {
            // 含 U+0000 的 id 按规范忽略本次设置，LastEventId 保持原值（这里仍为 ""）。
            // 用 (char)0 拼接，避免源码出现裸 NUL 字节。
            var e = Assert.Single(Parse("id: a" + (char)0 + "b\ndata: x\n\n"));
            Assert.Equal("", e.LastEventId);
        }

        [Fact]
        public void RetryField_NumericValue_Parsed()
        {
            var parser = new SseEventParser();
            var bytes = Encoding.UTF8.GetBytes("retry: 3000\n");
            parser.Feed(bytes, 0, bytes.Length, _ => { });
            Assert.Equal(3000, parser.RetryMilliseconds);
        }

        [Fact]
        public void RetryField_NonNumericValue_Ignored()
        {
            var parser = new SseEventParser();
            var bytes = Encoding.UTF8.GetBytes("retry: 30x0\n");
            parser.Feed(bytes, 0, bytes.Length, _ => { });
            Assert.Null(parser.RetryMilliseconds);
        }

        [Fact]
        public void CommentLines_Ignored()
        {
            Assert.Empty(Parse(": keep-alive\n"));
            // 注释不影响后续事件
            Assert.Equal("x", Assert.Single(Parse(": comment\ndata: x\n\n")).Data);
        }

        [Fact]
        public void Bom_StrippedAtStreamStart()
        {
            var input = new List<byte> { 0xEF, 0xBB, 0xBF };
            input.AddRange(Encoding.UTF8.GetBytes("data: x\n\n"));
            var e = Assert.Single(ParseBytes(input.ToArray()));
            Assert.Equal("x", e.Data);
        }

        [Fact]
        public void Bom_SplitAcrossChunks_StillStripped()
        {
            var tail = new List<byte> { 0xBF };
            tail.AddRange(Encoding.UTF8.GetBytes("data: x\n\n"));
            var e = Assert.Single(ParseBytes(new byte[] { 0xEF, 0xBB }, tail.ToArray()));
            Assert.Equal("x", e.Data);
        }

        [Theory]
        [InlineData("data: x\n\n")]
        [InlineData("data: x\r\r")]
        [InlineData("data: x\r\n\r\n")]
        public void LineEndings_AllThreeForms_Equivalent(string input)
        {
            Assert.Equal("x", Assert.Single(Parse(input)).Data);
        }

        [Fact]
        public void CrLf_SplitAcrossChunks_NoPhantomBlankLine()
        {
            // chunk 边界恰好落在 \r 和 \n 之间，不能产生多余空行（多余 dispatch）
            var events = Parse("data: x\r", "\n\ndata: y\r\n\r\n");
            Assert.Equal(2, events.Count);
            Assert.Equal("x", events[0].Data);
            Assert.Equal("y", events[1].Data);
        }

        [Fact]
        public void Utf8MultiByte_SplitAcrossChunks_DecodedCorrectly()
        {
            // "中" = E4 B8 AD，切成 chunk1 末尾两字节 + chunk2 首字节
            var chunk1 = new List<byte>(Encoding.UTF8.GetBytes("data: ")) { 0xE4, 0xB8 };
            var chunk2 = new List<byte> { 0xAD };
            chunk2.AddRange(Encoding.UTF8.GetBytes("\n\n"));
            var e = Assert.Single(ParseBytes(chunk1.ToArray(), chunk2.ToArray()));
            Assert.Equal("中", e.Data);
        }

        [Fact]
        public void UnknownField_Ignored()
        {
            var e = Assert.Single(Parse("foo: bar\ndata: x\n\n"));
            Assert.Equal("x", e.Data);
        }

        [Fact]
        public void Reset_PreservesLastEventId_DropsHalfEvent()
        {
            var parser = new SseEventParser();
            var events = new List<SseEvent>();
            var first = Encoding.UTF8.GetBytes("id: 7\ndata: x\n\n");
            parser.Feed(first, 0, first.Length, events.Add);

            // 喂入半个事件后 Reset，应丢弃半事件但保留 LastEventId
            var partial = Encoding.UTF8.GetBytes("data: dropped");
            parser.Feed(partial, 0, partial.Length, events.Add);
            parser.Reset();

            var second = Encoding.UTF8.GetBytes("data: y\n\n");
            parser.Feed(second, 0, second.Length, events.Add);

            Assert.Equal(2, events.Count);
            Assert.Equal("x", events[0].Data);
            Assert.Equal("y", events[1].Data);
            Assert.Equal("7", events[1].LastEventId); // Reset 保留 id
        }

        [Fact]
        public void IdField_NotConfirmedUntilDispatch()
        {
            // 半事件里的 id（无空行）不应推进确认的 LastEventId
            // （规范：last event ID string 仅在 dispatch 时从 buffer 同步）
            var parser = new SseEventParser();
            var bytes = Encoding.UTF8.GetBytes("id: 8\n");
            parser.Feed(bytes, 0, bytes.Length, _ => { });
            Assert.Equal("", parser.LastEventId);
        }

        [Fact]
        public void IdField_WithoutData_StillConfirmsOnBlankLine()
        {
            // 只有 id、无 data 的事件块：dispatch 仍同步 last event id（即使不触发事件）
            var parser = new SseEventParser();
            var bytes = Encoding.UTF8.GetBytes("id: 8\n\n");
            var count = 0;
            parser.Feed(bytes, 0, bytes.Length, _ => count++);
            Assert.Equal(0, count);                // 无 data，不 dispatch 事件
            Assert.Equal("8", parser.LastEventId); // 但确认 id 已更新
        }

        [Fact]
        public void IdField_InHalfEvent_DiscardedByReset()
        {
            // 半事件含 id，Reset 后应丢弃该 id（回退到上一个已确认值）
            var parser = new SseEventParser();
            var events = new List<SseEvent>();
            var first = Encoding.UTF8.GetBytes("id: 3\ndata: x\n\n"); // 确认 id=3
            parser.Feed(first, 0, first.Length, events.Add);
            var half = Encoding.UTF8.GetBytes("id: 8\ndata: dropped"); // 半事件，id=8 未确认
            parser.Feed(half, 0, half.Length, events.Add);
            parser.Reset();
            var next = Encoding.UTF8.GetBytes("data: y\n\n");
            parser.Feed(next, 0, next.Length, events.Add);

            Assert.Equal(2, events.Count);
            Assert.Equal("3", events[1].LastEventId); // 回退到 3，而非半事件的 8
        }

        [Fact]
        public void RetryField_Overflow_ClampsToIntMax()
        {
            var parser = new SseEventParser();
            var bytes = Encoding.UTF8.GetBytes("retry: 99999999999999\n");
            parser.Feed(bytes, 0, bytes.Length, _ => { });
            Assert.Equal(int.MaxValue, parser.RetryMilliseconds);
        }

        [Theory]
        [InlineData(-1, 0)]
        [InlineData(0, -1)]
        [InlineData(0, 5)]  // offset 0 + count 5 > buffer(3)
        [InlineData(2, 2)]  // offset 2 + count 2 > buffer(3)
        public void Feed_InvalidRange_Throws(int offset, int count)
        {
            var parser = new SseEventParser();
            var buf = new byte[3];
            Assert.Throws<ArgumentOutOfRangeException>(
                () => parser.Feed(buf, offset, count, _ => { }));
        }

        [Fact]
        public void LongLineWithoutTerminator_GrowsAndParses()
        {
            // 长行触发多次扩容，应正确解析、不丢字节
            var big = new string('a', 200_000);
            var e = Assert.Single(Parse("data: " + big + "\n\n"));
            Assert.Equal(big, e.Data);
        }

        [Fact]
        public void Reset_ReenablesBomStripping_ForNewStream()
        {
            // Reset 后视作新流：流首 BOM 应再次被剥离，而非泄漏进首行（跨连接复用的必要保证）
            var parser = new SseEventParser();
            var events = new List<SseEvent>();

            var s1 = new List<byte> { 0xEF, 0xBB, 0xBF };
            s1.AddRange(Encoding.UTF8.GetBytes("data: a\n\n"));
            parser.Feed(s1.ToArray(), 0, s1.Count, events.Add);

            parser.Reset();

            var s2 = new List<byte> { 0xEF, 0xBB, 0xBF };
            s2.AddRange(Encoding.UTF8.GetBytes("data: b\n\n"));
            parser.Feed(s2.ToArray(), 0, s2.Count, events.Add);

            Assert.Equal(2, events.Count);
            Assert.Equal("a", events[0].Data);
            Assert.Equal("b", events[1].Data); // 第二条流的 BOM 也被剥离
        }
    }
}
