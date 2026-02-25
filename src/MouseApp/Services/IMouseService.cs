using System.Threading.Tasks;
using MouseApp.Models;

namespace MouseApp.Services;

public interface IMouseService
{
    Task ApplyProfileAsync(MouseProfile profile);
}
