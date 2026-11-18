using Microsoft.AspNetCore.Components.Web;

namespace ErrorLensLLM.Dashboard;

/// <summary>Keyboard activation for elements that act like buttons but are not (table rows that open).</summary>
public static class Keys
{
    public static bool Activates(KeyboardEventArgs key) => key.Key is "Enter" or " ";
}
