using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Auction.Integration.Tests;

[Collection(SqlServerCollection.Name)]
public class ImageManagerTests(SqlServerFixture sql, RedisFixture redis) : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "auction-images-" + Guid.NewGuid().ToString("N"));
    private AuctionDbContext _db = null!;
    private Guid _sellerId;
    private Guid _itemId;

    public async Task InitializeAsync()
    {
        _db = await sql.CreateEmptyContextAsync("images");
        await _db.Database.MigrateAsync();

        var now = DateTime.UtcNow;
        var seller = new User
        {
            UserId = Guid.NewGuid(), Username = "seller1", Email = "seller1@test.local", PasswordHash = "x",
            Status = UserStatus.Active, CreatedAt = now, UpdatedAt = now,
        };
        seller.Roles.Add(new UserRole { UserId = seller.UserId, Role = Role.Seller, GrantedAt = now });
        var category = new Category { Name = "Art", Slug = "art" };
        _db.Users.Add(seller);
        _db.Categories.Add(category);
        await _db.SaveChangesAsync();

        _db.SellerProfiles.Add(new SellerProfile { UserId = seller.UserId, DisplayName = "Shop", Status = SellerStatus.Verified });
        var item = new Item
        {
            ItemId = Guid.NewGuid(), SellerId = seller.UserId, CategoryId = category.CategoryId, Title = "Print", Description = "d",
            ShippingType = ShippingType.FreeShipping, Status = ItemStatus.Draft, CreatedAt = now, UpdatedAt = now,
        };
        _db.Items.Add(item);
        await _db.SaveChangesAsync();

        _sellerId = seller.UserId;
        _itemId = item.ItemId;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ImageManager Manager() =>
        new(_db, new SellerGuard(_db, TestServices.StatusCache(_db, redis.Connection)), new ImageStoreOptions { RootPath = _root }, TimeProvider.System);

    private static byte[] MakePng(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(200, 30, 30));
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    // Splits bytes into 64 KB chunks, like the gRPC stream does.
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks(byte[] bytes)
    {
        const int size = 64 * 1024;
        for (var i = 0; i < bytes.Length; i += size)
        {
            await Task.Yield();
            yield return bytes.AsMemory(i, Math.Min(size, bytes.Length - i));
        }
    }

    private async Task<DomainException> FailsWith(Func<Task> act, string code)
    {
        var ex = (await act.Should().ThrowAsync<DomainException>()).Which;
        ex.Code.Should().Be(code);
        return ex;
    }

    [Fact]
    public async Task Png_upload_is_stored_with_a_thumbnail()
    {
        var png = MakePng(1200, 800);

        var row = await Manager().UploadAsync(_sellerId, new UploadHeader(_itemId, "image/png", png.Length), Chunks(png));

        row.Status.Should().Be(ImageStatus.Ready);
        row.Width.Should().Be(1200);
        row.Height.Should().Be(800);
        row.StorageKey.Should().Be($"{_itemId}/{row.ImageId}.png");

        var full = await Manager().FindAsync(row.ImageId, thumb: false);
        var thumb = await Manager().FindAsync(row.ImageId, thumb: true);
        full!.ContentType.Should().Be("image/png");
        (await File.ReadAllBytesAsync(full.Path)).Should().Equal(png);

        var thumbInfo = await Image.IdentifyAsync(thumb!.Path);
        thumbInfo.Width.Should().Be(400);
        thumbInfo.Height.Should().BeLessOrEqualTo(400);

        Directory.GetFiles(Path.Combine(_root, "tmp")).Should().BeEmpty("the temp file is moved into place");
    }

    [Fact]
    public async Task Exe_renamed_to_png_is_UPLOAD_INVALID()
    {
        var exe = new byte[2048];
        "MZ"u8.CopyTo(exe);   // start of every Windows program

        await FailsWith(() => Manager().UploadAsync(_sellerId, new UploadHeader(_itemId, "image/png", exe.Length), Chunks(exe)), ErrorCodes.UploadInvalid);

        (await _db.ItemImages.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Six_megabytes_is_UPLOAD_INVALID()
    {
        var big = new byte[6 * 1024 * 1024];
        MakePng(10, 10).CopyTo(big, 0);

        await FailsWith(() => Manager().UploadAsync(_sellerId, new UploadHeader(_itemId, "image/png", big.Length), Chunks(big)), ErrorCodes.UploadInvalid);
    }

    [Fact]
    public async Task Lying_about_the_size_is_still_stopped_at_5_megabytes()
    {
        var big = new byte[6 * 1024 * 1024];
        MakePng(10, 10).CopyTo(big, 0);

        // Header says 1 KB, stream sends 6 MB.
        await FailsWith(() => Manager().UploadAsync(_sellerId, new UploadHeader(_itemId, "image/png", 1024), Chunks(big)), ErrorCodes.UploadInvalid);

        Directory.GetFiles(Path.Combine(_root, "tmp")).Should().BeEmpty("the half-written file is removed");
    }

    [Fact]
    public async Task Jpeg_bytes_sent_as_png_is_UPLOAD_INVALID()
    {
        using var image = new Image<Rgba32>(20, 20);
        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream);
        var jpeg = stream.ToArray();

        await FailsWith(() => Manager().UploadAsync(_sellerId, new UploadHeader(_itemId, "image/png", jpeg.Length), Chunks(jpeg)), ErrorCodes.UploadInvalid);
    }

    [Fact]
    public async Task Gif_type_is_UPLOAD_INVALID()
    {
        var png = MakePng(10, 10);

        await FailsWith(() => Manager().UploadAsync(_sellerId, new UploadHeader(_itemId, "image/gif", png.Length), Chunks(png)), ErrorCodes.UploadInvalid);
    }

    [Fact]
    public async Task Someone_elses_item_is_NOT_OWNER()
    {
        var other = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var user = new User
        {
            UserId = other, Username = "seller2", Email = "seller2@test.local", PasswordHash = "x",
            Status = UserStatus.Active, CreatedAt = now, UpdatedAt = now,
        };
        user.Roles.Add(new UserRole { UserId = other, Role = Role.Seller, GrantedAt = now });
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        _db.SellerProfiles.Add(new SellerProfile { UserId = other, DisplayName = "Other", Status = SellerStatus.Verified });
        await _db.SaveChangesAsync();
        var png = MakePng(10, 10);

        await FailsWith(() => Manager().UploadAsync(other, new UploadHeader(_itemId, "image/png", png.Length), Chunks(png)), ErrorCodes.NotOwner);
    }

    [Fact]
    public async Task Unknown_image_is_not_found()
    {
        (await Manager().FindAsync(Guid.NewGuid(), thumb: false)).Should().BeNull();
    }
}
