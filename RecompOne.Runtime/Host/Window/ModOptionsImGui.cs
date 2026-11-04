using ImGuiNET;
using RecompOne.Runtime.Modding;

namespace RecompOne.Runtime.Host.Window;

/// <summary>
/// Draws declarative <see cref="ModOption"/>s as ImGui widgets, so a mod that describes its
/// settings gets a desktop UI without writing any ImGui itself. The Android host renders the
/// same descriptions as native MenuSheet rows.
/// </summary>
public static class ModOptionsImGui
{
    public static void Draw(IReadOnlyList<ModOption> options)
    {
        string? section = null;

        foreach (var opt in options)
        {
            if (opt.Section != section)
            {
                section = opt.Section;
                if (!string.IsNullOrEmpty(section))
                {
                    ImGui.Spacing();
                    ImGui.SeparatorText(section);
                }
            }

            bool available = Available(opt);
            if (!available) ImGui.BeginDisabled();

            ImGui.PushID(opt.Id);
            try { DrawOne(opt); }
            catch (Exception ex) { ImGui.TextDisabled($"({opt.Label}: {ex.Message})"); }
            ImGui.PopID();

            if (!available) ImGui.EndDisabled();

            if (!string.IsNullOrEmpty(opt.Description) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(opt.Description);
        }
    }

    static bool Available(ModOption opt)
    {
        try { return opt.Available; }
        catch { return true; }
    }

    static void DrawOne(ModOption opt)
    {
        switch (opt.Kind)
        {
            case ModOptionKind.Toggle:
            {
                bool value = opt.GetBool?.Invoke() ?? false;
                if (ImGui.Checkbox(opt.Label, ref value)) opt.SetBool?.Invoke(value);
                break;
            }

            case ModOptionKind.Choice:
            {
                if (opt.Choices.Length == 0) break;
                int index = Math.Clamp(opt.GetIndex?.Invoke() ?? 0, 0, opt.Choices.Length - 1);
                if (ImGui.Combo(opt.Label, ref index, opt.Choices, opt.Choices.Length))
                    opt.SetIndex?.Invoke(index);
                break;
            }

            case ModOptionKind.Slider:
            {
                float value = opt.GetFloat?.Invoke() ?? 0f;
                // Integral ranges get an int slider so the readout has no stray decimals.
                if (opt.Step >= 1f && opt.Min == MathF.Floor(opt.Min) && opt.Max == MathF.Floor(opt.Max))
                {
                    int i = (int)MathF.Round(value);
                    if (ImGui.SliderInt(opt.Label, ref i, (int)opt.Min, (int)opt.Max))
                        opt.SetFloat?.Invoke(Snap(i, opt));
                }
                else if (ImGui.SliderFloat(opt.Label, ref value, opt.Min, opt.Max, opt.Format))
                {
                    opt.SetFloat?.Invoke(Snap(value, opt));
                }
                break;
            }

            case ModOptionKind.Action:
            {
                if (ImGui.Button(opt.Label)) opt.Invoke?.Invoke();
                break;
            }
        }
    }

    static float Snap(float value, ModOption opt)
    {
        if (opt.Step <= 0f) return Math.Clamp(value, opt.Min, opt.Max);
        float snapped = opt.Min + MathF.Round((value - opt.Min) / opt.Step) * opt.Step;
        return Math.Clamp(snapped, opt.Min, opt.Max);
    }
}
