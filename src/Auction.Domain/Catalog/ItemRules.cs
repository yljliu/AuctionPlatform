using Auction.Domain.Enums;
using Auction.Domain.Errors;

namespace Auction.Domain.Catalog;

/// <summary>What the seller sends for an item (enum values arrive as text).</summary>
public sealed record ItemInput(
    int CategoryId,
    string? Title,
    string? Description,
    string? Condition,
    string? WarrantyType,
    string? WarrantyNote,
    string? ShippingType,
    decimal? ShippingCost,
    int HandlingDays,
    bool ReturnsAccepted,
    int? ReturnWindowDays,
    string? ReturnShippingPaidBy);

/// <summary>An item that passed every rule.</summary>
public sealed record ValidItem(
    int CategoryId,
    string Title,
    string Description,
    ItemCondition Condition,
    WarrantyType WarrantyType,
    string? WarrantyNote,
    ShippingType ShippingType,
    decimal? ShippingCost,
    byte HandlingDays,
    bool ReturnsAccepted,
    byte? ReturnWindowDays,
    ReturnShippingPaidBy? ReturnShippingPaidBy);

/// <summary>Field rules for CreateItem and UpdateItem (R2).</summary>
public static class ItemRules
{
    public const int TitleMax = 120;
    public const int DescriptionMax = 20000;
    public const int WarrantyNoteMax = 400;
    public const int HandlingDaysMax = 30;
    public const int ReturnWindowMin = 1;
    public const int ReturnWindowMax = 60;

    /// <summary>Returns the clean item, or throws VALIDATION_FAILED with one issue per bad field.</summary>
    public static ValidItem Validate(ItemInput input)
    {
        var issues = new List<FieldIssue>();
        var title = (input.Title ?? "").Trim();
        var description = (input.Description ?? "").Trim();
        var note = string.IsNullOrWhiteSpace(input.WarrantyNote) ? null : input.WarrantyNote.Trim();

        if (input.CategoryId <= 0)
        {
            issues.Add(new FieldIssue("category_id", "required", "Pick a category."));
        }

        if (title.Length == 0 || title.Length > TitleMax)
        {
            issues.Add(new FieldIssue("title", "length", $"Title must be 1 to {TitleMax} characters."));
        }

        if (description.Length == 0 || description.Length > DescriptionMax)
        {
            issues.Add(new FieldIssue("description", "length", $"Description must be 1 to {DescriptionMax} characters."));
        }

        var condition = ParseEnum<ItemCondition>(input.Condition, "condition", issues);
        var warranty = ParseEnum<WarrantyType>(input.WarrantyType, "warranty_type", issues);
        var shipping = ParseEnum<ShippingType>(input.ShippingType, "shipping_type", issues);

        if (note != null && note.Length > WarrantyNoteMax)
        {
            issues.Add(new FieldIssue("warranty_note", "length", $"Warranty note must be at most {WarrantyNoteMax} characters."));
        }

        if (shipping == ShippingType.BuyerPays && input.ShippingCost == null)
        {
            issues.Add(new FieldIssue("shipping_cost", "required", "Enter a shipping cost when the buyer pays shipping."));
        }

        if (input.ShippingCost < 0)
        {
            issues.Add(new FieldIssue("shipping_cost", "min", "Shipping cost cannot be negative."));
        }

        if (input.HandlingDays < 0 || input.HandlingDays > HandlingDaysMax)
        {
            issues.Add(new FieldIssue("handling_days", "range", $"Handling days must be 0 to {HandlingDaysMax}."));
        }

        ReturnShippingPaidBy? returnPaidBy = null;
        if (input.ReturnsAccepted)
        {
            if (input.ReturnWindowDays == null)
            {
                issues.Add(new FieldIssue("return_window_days", "required", "Enter how many days the buyer has to return it."));
            }
            else if (input.ReturnWindowDays < ReturnWindowMin || input.ReturnWindowDays > ReturnWindowMax)
            {
                issues.Add(new FieldIssue("return_window_days", "range", $"Return window must be {ReturnWindowMin} to {ReturnWindowMax} days."));
            }

            if (!string.IsNullOrWhiteSpace(input.ReturnShippingPaidBy))
            {
                returnPaidBy = ParseEnum<ReturnShippingPaidBy>(input.ReturnShippingPaidBy, "return_shipping_paid_by", issues);
            }
        }

        if (issues.Count > 0)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Some item fields are not valid.", issues);
        }

        // Free shipping and pickup have no cost to store.
        var cost = shipping == ShippingType.BuyerPays ? input.ShippingCost : null;

        return new ValidItem(
            input.CategoryId, title, description, condition, warranty, note, shipping, cost,
            (byte)input.HandlingDays, input.ReturnsAccepted,
            input.ReturnsAccepted ? (byte?)input.ReturnWindowDays : null,
            input.ReturnsAccepted ? returnPaidBy : null);
    }

    /// <summary>
    /// After the first bid (LockedAt set) these fields may not change (R2.5).
    /// Shipping and returns can still change.
    /// </summary>
    public static void EnsureLockedFieldsUnchanged(bool locked, ValidItem current, ValidItem next)
    {
        if (!locked)
        {
            return;
        }

        if (current.Title != next.Title
            || current.Description != next.Description
            || current.Condition != next.Condition
            || current.CategoryId != next.CategoryId)
        {
            throw new DomainException(ErrorCodes.EditLocked,
                "Title, description, condition and category cannot change after the first bid.");
        }
    }

    private static T ParseEnum<T>(string? value, string field, List<FieldIssue> issues) where T : struct, Enum
    {
        if (EnumText.TryParse<T>(value, out var parsed))
        {
            return parsed;
        }

        issues.Add(new FieldIssue(field, "one_of", $"Must be one of: {string.Join(", ", Enum.GetNames<T>())}."));
        return default;
    }
}

/// <summary>Parses enum names from text. Numbers like "3" are refused, so only real names pass.</summary>
public static class EnumText
{
    public static bool TryParse<T>(string? value, out T result) where T : struct, Enum
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value) || char.IsDigit(value.Trim()[0]) || value.Trim()[0] == '-')
        {
            return false;
        }

        return Enum.TryParse(value.Trim(), ignoreCase: true, out result) && Enum.IsDefined(result);
    }
}
