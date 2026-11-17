using Auction.Contracts;
using FluentAssertions;
using Google.Protobuf;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>Spec 02 task 1: the catalog and seller contracts exist and keep private data out.</summary>
public class CatalogContractTests
{
    [Fact]
    public void Catalog_and_seller_services_have_the_expected_methods()
    {
        CatalogService.Descriptor.Methods.Select(m => m.Name).Should().BeEquivalentTo(
            "GetCategoryTree", "ListCategories", "ListAuctions", "SearchAuctions", "GetAuction", "GetImage");

        SellerService.Descriptor.Methods.Select(m => m.Name).Should().BeEquivalentTo(
            "CreateItem", "UpdateItem", "DeleteDraftItem", "ListMyItems", "UploadImage",
            "CreateAuction", "UpdateAuction", "CancelAuction", "ListMyAuctions");
    }

    [Fact]
    public void Public_auction_detail_has_no_reserve_or_max_fields()
    {
        // R7.2: the public view must not be able to carry the reserve or any bidder's max.
        var allFieldNames = FieldNamesDeep(AuctionDetail.Descriptor);

        allFieldNames.Should().NotContain(n => n.Contains("reserve_price"));
        allFieldNames.Should().NotContain(n => n.Contains("max") && n != "max_total_extension_sec" && n != "my_max");
    }

    [Fact]
    public void Seller_view_adds_the_reserve()
    {
        SellerAuction.Descriptor.FindFieldByName("reserve_price").Should().NotBeNull();
    }

    [Fact]
    public void Paging_fields_exist_on_browse()
    {
        ListAuctionsRequest.Descriptor.FindFieldByName("page").Should().NotBeNull();
        ListAuctionsRequest.Descriptor.FindFieldByName("page_size").Should().NotBeNull();
        ListAuctionsResponse.Descriptor.FindFieldByName("total").Should().NotBeNull();
        ListAuctionsResponse.Descriptor.FindFieldByName("prices_delayed").Should().NotBeNull();
        AuctionCard.Descriptor.FindFieldByName("prices_delayed").Should().NotBeNull();
    }

    [Fact]
    public void Upload_chunk_is_header_or_data()
    {
        var header = new UploadImageChunk { Header = new UploadImageHeader { ItemId = "x", ContentType = "image/png", Bytes = 10 } };
        var data = new UploadImageChunk { Data = ByteString.CopyFrom(1, 2, 3) };

        header.PartCase.Should().Be(UploadImageChunk.PartOneofCase.Header);
        data.PartCase.Should().Be(UploadImageChunk.PartOneofCase.Data);
    }

    // Field names of a message and every message inside it, except my_status (the caller's own data).
    private static List<string> FieldNamesDeep(Google.Protobuf.Reflection.MessageDescriptor root)
    {
        var names = new List<string>();
        var seen = new HashSet<string>();
        var todo = new Stack<Google.Protobuf.Reflection.MessageDescriptor>();
        todo.Push(root);

        while (todo.Count > 0)
        {
            var message = todo.Pop();
            if (!seen.Add(message.FullName))
            {
                continue;
            }

            foreach (var field in message.Fields.InDeclarationOrder())
            {
                if (field.Name == "my_status")
                {
                    continue;
                }

                names.Add(field.Name);
                if (field.FieldType == Google.Protobuf.Reflection.FieldType.Message)
                {
                    todo.Push(field.MessageType);
                }
            }
        }

        return names;
    }
}
