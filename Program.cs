
Console.WriteLine();
Console.WriteLine("=== Ctrl 行の占有範囲 ===");
var ctrlRow = layout.Rows[5];
var off = 0.0;
foreach (var k in ctrlRow.Keys)
{
    if (!k.Spacer) Console.WriteLine($"  {k.Label,-6} {off,6:0.00} - {off + k.Width,6:0.00}");
    off += k.Width;
}