using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RetailStockDesk;

public partial class StockWindow : Window
{
    private readonly string _file = Path.Combine(AppContext.BaseDirectory, "inventory.json");
    private Inventory _inventory = new();

    public StockWindow()
    {
        InitializeComponent();
        Load();
        Refresh();
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(SavePreviewIfRequested));
    }

    private void SavePreviewIfRequested()
    {
        var path = Environment.GetEnvironmentVariable("STOCKROOM_SCREENSHOT_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var surface = (FrameworkElement)Content;
            var width = (int)surface.ActualWidth;
            var height = (int)surface.ActualHeight;
            if (width < 1 || height < 1) return;
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var output = File.Create(path);
            encoder.Save(output);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                _inventory = JsonSerializer.Deserialize<Inventory>(File.ReadAllText(_file)) ?? new();
                _inventory.Products ??= new();
                _inventory.Movements ??= new();
                return;
            }
        }
        catch (Exception ex) { Warn("Не удалось прочитать локальные данные: " + ex.Message); }
        _inventory = Inventory.Demo();
        Save();
    }

    private bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temporary = _file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_inventory, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _file, true);
            return true;
        }
        catch (Exception ex) { Warn("Не удалось сохранить данные: " + ex.Message); return false; }
    }

    private void Refresh(string? selectedSku = null)
    {
        var search = SearchBox.Text.Trim();
        var rows = _inventory.Products.Where(p => search.Length == 0 || p.Sku.Contains(search, StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || p.Category.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Quantity <= p.Minimum ? 0 : 1).ThenBy(p => p.Name).Select(p => new ProductRow(p)).ToList();
        ProductsGrid.ItemsSource = rows;
        if (selectedSku is not null) ProductsGrid.SelectedItem = rows.FirstOrDefault(p => p.Sku == selectedSku);
        CountText.Text = _inventory.Products.Count.ToString();
        UnitsText.Text = _inventory.Products.Sum(p => p.Quantity).ToString("N0");
        LowText.Text = _inventory.Products.Count(p => p.Quantity <= p.Minimum).ToString();
        FilterText.Text = $"Показано {rows.Count} из {_inventory.Products.Count}";
        HistoryGrid.ItemsSource = _inventory.Movements.OrderByDescending(m => m.Time).Take(50).ToList();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (ProductsGrid is not null && _inventory is not null) Refresh();
    }

    private void Product_Selected(object sender, SelectionChangedEventArgs e)
        => SelectedText.Text = (ProductsGrid.SelectedItem as ProductRow)?.Name ?? "Выберите товар в таблице";

    private void Receive_Click(object sender, RoutedEventArgs e) => Move(1);
    private void Ship_Click(object sender, RoutedEventArgs e) => Move(-1);

    private void Move(int direction)
    {
        if (ProductsGrid.SelectedItem is not ProductRow row) { Warn("Выберите товар в таблице."); return; }
        if (!int.TryParse(MoveBox.Text, out var count) || count < 1 || count > 1_000_000) { Warn("Введите положительное целое количество до 1 000 000."); return; }
        var product = _inventory.Products.First(p => p.Sku == row.Sku);
        if (direction < 0 && count > product.Quantity) { Warn("Для расхода недостаточно товара."); return; }
        if (direction > 0 && product.Quantity > int.MaxValue - count) { Warn("Слишком большое количество."); return; }
        product.Quantity += direction * count;
        var movement = new Movement { Time = DateTime.Now, Sku = product.Sku, ProductName = product.Name,
            Type = direction > 0 ? "Приход" : "Расход", Quantity = count, Note = NoteBox.Text.Trim() };
        _inventory.Movements.Add(movement);
        if (!Save()) { product.Quantity -= direction * count; _inventory.Movements.Remove(movement); return; }
        NoteBox.Clear();
        Refresh(product.Sku);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var sku = SkuBox.Text.Trim(); var name = NameBox.Text.Trim(); var category = CategoryBox.Text.Trim();
        if (sku.Length is < 2 or > 40 || name.Length is < 2 or > 100 || category.Length is < 2 or > 60)
        { Warn("Введите артикул, название и категорию (не менее 2 символов)."); return; }
        if (_inventory.Products.Any(p => p.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase))) { Warn("Этот артикул уже существует."); return; }
        if (!int.TryParse(QuantityBox.Text, out var quantity) || quantity < 0 || !int.TryParse(MinimumBox.Text, out var minimum) || minimum < 0)
        { Warn("Остаток и минимум должны быть неотрицательными целыми числами."); return; }
        var product = new Product { Sku = sku, Name = name, Category = category, Quantity = quantity, Minimum = minimum };
        _inventory.Products.Add(product);
        if (!Save()) { _inventory.Products.Remove(product); return; }
        SkuBox.Clear(); NameBox.Clear(); CategoryBox.Clear(); QuantityBox.Text = "0"; MinimumBox.Text = "5"; SearchBox.Clear();
        Refresh(sku);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Экспорт каталога", Filter = "CSV (*.csv)|*.csv", FileName = "stockroom-products.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var lines = new List<string> { "SKU,Name,Category,Quantity,Minimum,Status" };
            lines.AddRange(_inventory.Products.Select(p => string.Join(",", Csv(p.Sku), Csv(p.Name), Csv(p.Category),
                p.Quantity, p.Minimum, Csv(p.Quantity <= p.Minimum ? "Low stock" : "In stock"))));
            File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true));
            MessageBox.Show("CSV сохранён: " + dialog.FileName, "Stockroom");
        }
        catch (Exception ex) { Warn("Не удалось экспортировать CSV: " + ex.Message); }
    }

    private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    private static void Warn(string message) => MessageBox.Show(message, "Stockroom", MessageBoxButton.OK, MessageBoxImage.Warning);
}

public sealed class Inventory
{
    public List<Product> Products { get; set; } = new();
    public List<Movement> Movements { get; set; } = new();
    public static Inventory Demo() => new()
    {
        Products = new()
        {
            new() { Sku="SKU-1001", Name="Беспроводные наушники Nova", Category="Электроника", Quantity=34, Minimum=10 },
            new() { Sku="SKU-1002", Name="Настольная лампа Arc", Category="Освещение", Quantity=7, Minimum=12 },
            new() { Sku="SKU-1003", Name="Термокружка 450 мл", Category="Дом и кухня", Quantity=58, Minimum=15 },
            new() { Sku="SKU-1004", Name="Bluetooth-колонка Pulse", Category="Электроника", Quantity=3, Minimum=8 },
            new() { Sku="SKU-1005", Name="Блокнот A5 Linen", Category="Канцтовары", Quantity=92, Minimum=20 },
            new() { Sku="SKU-1006", Name="USB-C кабель 2 м", Category="Электроника", Quantity=18, Minimum=20 },
            new() { Sku="SKU-1007", Name="Органайзер Desk", Category="Дом и кухня", Quantity=41, Minimum=10 },
            new() { Sku="SKU-1008", Name="Подставка для ноутбука", Category="Аксессуары", Quantity=26, Minimum=8 }
        },
        Movements = new()
        {
            new() { Time=DateTime.Now.AddMinutes(-35), Type="Приход", Sku="SKU-1001", ProductName="Беспроводные наушники Nova", Quantity=12, Note="Поставка №18" },
            new() { Time=DateTime.Now.AddHours(-2), Type="Расход", Sku="SKU-1004", ProductName="Bluetooth-колонка Pulse", Quantity=5, Note="Заказ #2041" },
            new() { Time=DateTime.Now.AddHours(-4), Type="Расход", Sku="SKU-1003", ProductName="Термокружка 450 мл", Quantity=2, Note="Заказ #2040" }
        }
    };
}

public sealed class Product
{
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public int Quantity { get; set; }
    public int Minimum { get; set; }
}

public sealed class ProductRow(Product p)
{
    public string Sku { get; } = p.Sku;
    public string Name { get; } = p.Name;
    public string Category { get; } = p.Category;
    public int Quantity { get; } = p.Quantity;
    public int Minimum { get; } = p.Minimum;
    public string Status { get; } = p.Quantity <= p.Minimum ? "Пополнить" : "В наличии";
}

public sealed class Movement
{
    public DateTime Time { get; set; }
    public string Sku { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string Type { get; set; } = "";
    public int Quantity { get; set; }
    public string Note { get; set; } = "";
    public string TimeLabel => Time.ToString("dd.MM HH:mm");
}
