using Auction.Contracts;
using Auction.Infrastructure.Catalog;
using ImageEntity = Auction.Infrastructure.Persistence.Entities.ItemImage;
using ItemEntity = Auction.Infrastructure.Persistence.Entities.Item;

namespace Auction.Api.Services;

/// <summary>Turns catalog rows into proto messages.</summary>
public static class CatalogMapper
{
    public static Item ToProto(ItemWithImages source)
    {
        var item = source.Item;
        var proto = new Item
        {
            ItemId = item.ItemId.ToString(),
            SellerId = item.SellerId.ToString(),
            CategoryId = item.CategoryId,
            Title = item.Title,
            Description = item.Description,
            Condition = item.Condition.ToString(),
            WarrantyType = item.WarrantyType.ToString(),
            WarrantyNote = item.WarrantyNote ?? "",
            ShippingType = item.ShippingType.ToString(),
            ShippingCost = item.ShippingCost == null ? null : new Domain.Money(item.ShippingCost.Value, "USD").ToProto(),
            HandlingDays = item.HandlingDays,
            ReturnsAccepted = item.ReturnsAccepted,
            ReturnWindowDays = item.ReturnWindowDays,
            ReturnShippingPaidBy = item.ReturnShippingPaidBy?.ToString() ?? "",
            Status = item.Status.ToString(),
            LockedAtMs = item.LockedAt == null ? 0 : ToUnixMs(item.LockedAt.Value),
            CreatedAtMs = ToUnixMs(item.CreatedAt),
            UpdatedAtMs = ToUnixMs(item.UpdatedAt),
            RowVersion = Convert.ToBase64String(item.RowVersion),
        };

        foreach (var image in source.Images)
        {
            proto.Images.Add(ToProto(image));
        }

        return proto;
    }

    public static Image ToProto(ImageEntity image) => new()
    {
        ImageId = image.ImageId.ToString(),
        Url = $"/img/{image.ImageId}",
        ThumbUrl = $"/img/{image.ImageId}/thumb",
        ContentType = image.ContentType,
        Width = image.Width ?? 0,
        Height = image.Height ?? 0,
        SortOrder = image.SortOrder,
        Status = image.Status.ToString(),
    };

    /// <summary>Proto fields to the rule input. Missing Money and wrapper fields become null.</summary>
    public static Domain.Catalog.ItemInput ToInput(ItemFields? fields)
    {
        fields ??= new ItemFields();
        return new Domain.Catalog.ItemInput(
            fields.CategoryId,
            fields.Title,
            fields.Description,
            fields.Condition,
            fields.WarrantyType,
            fields.WarrantyNote,
            fields.ShippingType,
            fields.ShippingCost?.ToDomain().Amount,
            fields.HandlingDays,
            fields.ReturnsAccepted,
            fields.ReturnWindowDays,
            fields.ReturnShippingPaidBy);
    }

    public static long ToUnixMs(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    public static Guid ParseId(string value, string field) =>
        Guid.TryParse(value, out var id)
            ? id
            : throw new Domain.Errors.DomainException(Domain.Errors.ErrorCodes.ValidationFailed, $"{field} is not a valid id.",
                [new Domain.Errors.FieldIssue(field, "format", "Must be a UUID.")]);
}
