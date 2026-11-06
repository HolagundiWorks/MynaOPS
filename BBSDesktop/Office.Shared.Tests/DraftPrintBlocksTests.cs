// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Text;
using System.Text.Json;
using Xunit;

namespace Office.Shared.Tests;

public class DraftPrintBlocksTests
{
    [Fact]
    public void Reads_the_escaped_print_block_book()
    {
        var book = """{"v":1,"next":2,"items":[{"id":1,"name":"Plan","description":"Ground / Working drawing","handle":"AB"}]}""";
        var payload = "{\"version\":1,\"items\":{\"printblocks1\":" + JsonSerializer.Serialize(book) + "}}";
        var bytes = Encoding.UTF8.GetBytes("AAD_PROJECT\n" + payload);
        var list = DraftPrintBlocks.Read(bytes, "plan");
        var block = Assert.Single(list);
        Assert.Equal("plan", block.Drawing);
        Assert.Equal("Plan", block.Name);
        Assert.Equal("Ground / Working drawing", block.Description);
        Assert.Equal("AB", block.Handle);
    }

    [Fact]
    public void A_drawing_without_the_key_lists_nothing()
    {
        var bytes = Encoding.UTF8.GetBytes("LINE 0,0 10,0");
        Assert.Empty(DraftPrintBlocks.Read(bytes));
        Assert.Empty(DraftPrintBlocks.Read(null));
        Assert.Empty(DraftPrintBlocks.Read(Array.Empty<byte>()));
    }
}
