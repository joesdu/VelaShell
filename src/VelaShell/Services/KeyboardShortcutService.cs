using System.Runtime.InteropServices;

namespace VelaShell.Services;

/// <summary>
/// Resolves key combinations to shortcut actions, applying platform-specific
/// (macOS vs Windows/Linux) key mappings per context.
/// </summary>
public class KeyboardShortcutService : IKeyboardShortcutService
{
    private readonly Dictionary<(KeyModifiers, KeyCode, ShortcutContext), ShortcutAction> _mappings;

    /// <summary>
    /// Creates the service, auto-detecting whether the current OS is macOS.
    /// </summary>
    public KeyboardShortcutService()
        : this(RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) { }

    // ReSharper disable once InconsistentNaming
    /// <summary>
    /// Creates the service with an explicit platform flag (primarily for testing).
    /// </summary>
    /// <param name="isMacOS">True to use macOS key mappings; otherwise Windows/Linux mappings.</param>
    public KeyboardShortcutService(bool isMacOS)
    {
        IsMacOS = isMacOS;
        _mappings = [];
        RegisterMappings();
    }

    /// <summary>Whether macOS key mappings are in effect.</summary>
    public bool IsMacOS { get; }

    /// <summary>
    /// Resolves a key combination in a given context to its mapped shortcut action,
    /// falling back to global mappings when in the terminal context.
    /// </summary>
    /// <param name="modifiers">The active modifier keys.</param>
    /// <param name="key">The pressed key.</param>
    /// <param name="context">The context in which the key was pressed.</param>
    /// <returns>The mapped action, or <see cref="ShortcutAction.None"/> if unmapped.</returns>
    public ShortcutAction Resolve(KeyModifiers modifiers, KeyCode key, ShortcutContext context)
    {
        if (_mappings.TryGetValue((modifiers, key, context), out ShortcutAction action) || context == ShortcutContext.Terminal && _mappings.TryGetValue((modifiers, key, ShortcutContext.Global), out action))
        {
            return action;
        }
        return ShortcutAction.None;
    }

    private void RegisterMappings()
    {
        KeyModifiers primaryModifier = IsMacOS ? KeyModifiers.Meta : KeyModifiers.Ctrl;
        RegisterTerminalMappings();
        RegisterGlobalMappings(primaryModifier);
    }

    private void RegisterTerminalMappings()
    {
        // Ctrl+C always sends interrupt in terminal (both platforms)
        Map(KeyModifiers.Ctrl, KeyCode.C, ShortcutContext.Terminal, ShortcutAction.SendInterrupt);
        if (IsMacOS)
        {
            // macOS: Cmd+C = copy, Cmd+V = paste in terminal
            Map(KeyModifiers.Meta, KeyCode.C, ShortcutContext.Terminal, ShortcutAction.Copy);
            Map(KeyModifiers.Meta, KeyCode.V, ShortcutContext.Terminal, ShortcutAction.Paste);
        }
        else
        {
            // Win/Linux: Ctrl+Shift+C = copy, Ctrl+Shift+V = paste in terminal
            Map(KeyModifiers.Ctrl | KeyModifiers.Shift, KeyCode.C, ShortcutContext.Terminal, ShortcutAction.Copy);
            Map(KeyModifiers.Ctrl | KeyModifiers.Shift, KeyCode.V, ShortcutContext.Terminal, ShortcutAction.Paste);
        }
    }

    private void RegisterGlobalMappings(KeyModifiers primaryModifier)
    {
        Map(primaryModifier, KeyCode.T, ShortcutContext.Global, ShortcutAction.NewTab);
        Map(primaryModifier, KeyCode.W, ShortcutContext.Global, ShortcutAction.CloseTab);
        Map(primaryModifier, KeyCode.Comma, ShortcutContext.Global, ShortcutAction.OpenSettings);

        // Tab switching uses Ctrl on all platforms
        Map(KeyModifiers.Ctrl, KeyCode.Tab, ShortcutContext.Global, ShortcutAction.NextTab);
        Map(KeyModifiers.Ctrl | KeyModifiers.Shift, KeyCode.Tab, ShortcutContext.Global, ShortcutAction.PreviousTab);
    }

    private void Map(KeyModifiers modifiers, KeyCode key, ShortcutContext context, ShortcutAction action)
    {
        _mappings[(modifiers, key, context)] = action;
    }
}
