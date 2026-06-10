using System.Text;
using Dmart.Middleware;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Middleware;

// The strip middleware buffers only JSON responses (so it can drop empty
// object/string/array properties); everything else must stream straight
// through with no buffering — a 50MB attachment download must not be pulled
// into RAM first. SniffingBodyStream is the unit that makes that decision on
// the first write by inspecting Response.ContentType.
public sealed class JsonStripEmptiesMiddlewareTests
{
    // Inner stream that records each Write as a separate chunk, so a test can
    // prove whether bytes arrived incrementally (passthrough) or all at once.
    private sealed class RecordingStream : Stream
    {
        public List<byte[]> Chunks { get; } = new();
        public override bool CanWrite => true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => Chunks.Add(b[o..(o + c)]);
        public byte[] All() => Chunks.SelectMany(x => x).ToArray();
    }

    private static HttpResponse ResponseWithContentType(string? contentType)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.ContentType = contentType;
        return ctx.Response;
    }

    [Fact]
    public void NonJson_Response_Streams_Through_Without_Buffering()
    {
        var inner = new RecordingStream();
        var sniff = new SniffingBodyStream(inner, ResponseWithContentType("application/pdf"));

        sniff.Write(new byte[] { 1, 2, 3 }, 0, 3);
        sniff.Write(new byte[] { 4, 5 }, 0, 2);

        sniff.Buffered.ShouldBeFalse("non-JSON must not buffer");
        // Two writes arrived as two separate chunks at the inner stream — proves
        // they were not coalesced through a buffer.
        inner.Chunks.Count.ShouldBe(2);
        inner.All().ShouldBe(new byte[] { 1, 2, 3, 4, 5 });
    }

    [Fact]
    public void Json_Response_Is_Buffered_Not_Passed_Through()
    {
        var inner = new RecordingStream();
        var sniff = new SniffingBodyStream(inner, ResponseWithContentType("application/json; charset=utf-8"));

        sniff.Write(Encoding.UTF8.GetBytes("{\"a\":1}"), 0, 7);

        sniff.Buffered.ShouldBeTrue("JSON must buffer so empties can be stripped");
        inner.Chunks.ShouldBeEmpty("nothing should reach the inner stream until the middleware flushes the buffer");
        sniff.Buffer!.Length.ShouldBe(7);
    }

    [Fact]
    public void Unknown_ContentType_Falls_Back_To_Buffering()
    {
        // A handler that writes a body before setting Content-Type leaves it
        // null at first write. Buffering is the safe, today-equivalent default.
        var inner = new RecordingStream();
        var sniff = new SniffingBodyStream(inner, ResponseWithContentType(null));

        sniff.Write(new byte[] { 9 }, 0, 1);

        sniff.Buffered.ShouldBeTrue();
        inner.Chunks.ShouldBeEmpty();
    }

    [Fact]
    public void No_Write_Produces_No_Buffer_And_No_Output()
    {
        var inner = new RecordingStream();
        var sniff = new SniffingBodyStream(inner, ResponseWithContentType("application/json"));

        sniff.Buffered.ShouldBeFalse("a response with no body must not be treated as buffered");
        inner.Chunks.ShouldBeEmpty();
    }
}
