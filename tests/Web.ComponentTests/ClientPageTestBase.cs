using Microsoft.AspNetCore.Components.Forms;

namespace NexaVerify.Web.ComponentTests;

/// <summary>Helpers for client-portal page tests: fill MudBlazor fields by label, choose photos, click by test id.</summary>
public abstract class ClientPageTestBase : PageTestBase
{
    /// <summary>Starts like a real JPEG (the portal sniffs the first bytes) and is padded to a plausible size.</summary>
    protected static byte[] Jpeg(int length = 2048)
    {
        var bytes = new byte[length];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xE0;
        for (var i = 4; i < length; i++)
        {
            bytes[i] = (byte)(i % 251 + 1);
        }

        return bytes;
    }

    protected static void Fill<TComponent>(IRenderedComponent<TComponent> cut, string label, string text)
        where TComponent : class, Microsoft.AspNetCore.Components.IComponent
    {
        var field = cut.FindComponents<MudTextField<string>>().First(f => f.Instance.Label == label);
        field.Find("input,textarea").Input(text);
    }

    protected static void Click<TComponent>(IRenderedComponent<TComponent> cut, string testId)
        where TComponent : class, Microsoft.AspNetCore.Components.IComponent =>
        cut.Find($"[data-testid={testId}]").Click();

    /// <summary>Picks a photo in the upload tab and presses "Use this photo".</summary>
    protected static void ChoosePhoto<TComponent>(IRenderedComponent<TComponent> cut, byte[] bytes, string name = "photo.jpg", string contentType = "image/jpeg")
        where TComponent : class, Microsoft.AspNetCore.Components.IComponent
    {
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(bytes, name, null, contentType));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=capture-use]").Count.ShouldBe(1));
        cut.Find("[data-testid=capture-use]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=photo-ready]").Count.ShouldBe(1));
    }
}
