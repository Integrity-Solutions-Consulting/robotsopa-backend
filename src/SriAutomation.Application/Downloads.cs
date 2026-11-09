using SriAutomation.Domain;
namespace SriAutomation.Application;

public sealed record DownloadedVoucher(string Account, string Type, string Key, DateOnly Issued, byte[]? Xml, byte[]? Pdf);
public interface IDownloadPipeline
{
    Voucher Process(DownloadedVoucher download);
    bool IsAvailable(Voucher document, string extension) => false;
    byte[]? Pending(string account, string type, string key, DateOnly issued, string extension) => null;
    void Preserve(string account, string type, string key, DateOnly issued, string extension, byte[] bytes) { }
}
