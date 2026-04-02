using SLH.Models;

namespace SLH.Services;

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}
