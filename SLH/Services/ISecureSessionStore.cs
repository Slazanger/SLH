using SLH.Models;

namespace SLH.Services;

public interface ISecureSessionStore
{
    StoredSession? Load();
    void Save(StoredSession session);
    void Clear();
}
