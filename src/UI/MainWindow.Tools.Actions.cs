using System;
using System.IO;
using System.Windows.Forms;
namespace ai02;

public partial class MainWindow
{
    private void HandleExportConfiguration()
    {
        try
        {
            using var dialog = new SaveFileDialog
            {
                Title = "导出前线战术指挥配置",
                Filter = "JSON 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
                DefaultExt = "json",
                AddExtension = true,
                OverwritePrompt = true,
                InitialDirectory = ResolveConfigurationTransferDirectory(),
                FileName = $"frontline-commander-config-{DateTime.Now:yyyyMMdd-HHmmss}.json"
            };

            if (dialog.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
            {
                configurationTransferStatus = "配置导出已取消。";
                return;
            }

            plugin.Configuration.ExportToFile(dialog.FileName, out configurationTransferStatus);
        }
        catch (Exception ex)
        {
            configurationTransferStatus = $"配置导出失败：{ex.Message}";
        }
    }

    private void HandleImportConfiguration()
    {
        try
        {
            using var dialog = new OpenFileDialog
            {
                Title = "导入前线战术指挥配置",
                Filter = "JSON 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                InitialDirectory = ResolveConfigurationTransferDirectory()
            };

            if (dialog.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
            {
                configurationTransferStatus = "配置导入已取消。";
                return;
            }

            plugin.Configuration.TryImportFromFile(dialog.FileName, out configurationTransferStatus);
        }
        catch (Exception ex)
        {
            configurationTransferStatus = $"配置导入失败：{ex.Message}";
        }
    }

    private static string ResolveConfigurationTransferDirectory()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents) && Directory.Exists(documents))
            return documents;

        return AppContext.BaseDirectory;
    }
}