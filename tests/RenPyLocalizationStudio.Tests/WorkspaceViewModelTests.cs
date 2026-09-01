using System.Windows;
using RenPyLocalizationStudio.App.Services;
using RenPyLocalizationStudio.App.ViewModels;
using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.Tests;

public sealed class WorkspaceViewModelTests
{
    [Fact]
    public void PatchDocumentHost_相同稳定Id只保留一个标签()
    {
        var confirmation = new FakeConfirmationService();
        var host = new PatchDocumentHostViewModel(confirmation);
        var first = new TestDocument("diff:zzz", "第一次");
        var second = new TestDocument("diff:zzz", "第二次");

        host.AddOrActivate(first);
        host.AddOrActivate(second);

        Assert.Single(host.Documents);
        Assert.Same(first, host.SelectedDocument);
    }

    [Fact]
    public void PatchDocumentHost_脏文档拒绝确认时不会关闭()
    {
        var confirmation = new FakeConfirmationService { ConfirmResult = false };
        var host = new PatchDocumentHostViewModel(confirmation);
        var document = new TestDocument("diff:replace", "Replace Diff");
        document.MarkDirty();
        host.AddOrActivate(document);

        host.CloseDocumentCommand.Execute(document);

        Assert.Contains(document, host.Documents);
        Assert.Equal(1, confirmation.ConfirmationCount);
    }

    [Fact]
    public void PatchDocumentHost_切换项目时清理临时Diff但保留固定文档()
    {
        var confirmation = new FakeConfirmationService();
        var host = new PatchDocumentHostViewModel(confirmation);
        var fixedDocument = new TestDocument("zzz", "zzz", canClose: false);
        var diff = new TestDocument("diff:old", "旧项目 Diff");
        host.Documents.Add(fixedDocument);
        host.AddOrActivate(diff);

        host.ClearTransientDocuments();

        Assert.Contains(fixedDocument, host.Documents);
        Assert.DoesNotContain(diff, host.Documents);
        Assert.Same(fixedDocument, host.SelectedDocument);
    }

    [Fact]
    public async Task TaskCenter_取消后清理Busy状态()
    {
        var tasks = new TaskCenterViewModel();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = tasks.RunAsync("测试任务", async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });

        await started.Task;
        tasks.CancelCommand.Execute(null);
        await run;

        Assert.False(tasks.IsBusy);
        Assert.Equal("操作已取消。", tasks.StatusMessage);
    }

    [Fact]
    public async Task TaskCenter_新任务不会被旧任务的finally清除Busy状态()
    {
        var tasks = new TaskCenterViewModel();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = tasks.RunAsync("第一个任务", async token =>
        {
            firstStarted.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { await firstCleanup.Task; }
        });

        await firstStarted.Task;
        var second = tasks.RunAsync("第二个任务", async token => await Task.Delay(Timeout.InfiniteTimeSpan, token));
        await Task.Delay(50);
        Assert.True(tasks.IsBusy);

        firstCleanup.SetResult();
        tasks.CancelCommand.Execute(null);
        await Task.WhenAll(first, second);

        Assert.False(tasks.IsBusy);
        Assert.Equal("操作已取消。", tasks.StatusMessage);
    }

    [Fact]
    public void ImagePreviewViewModel_ContextUpdate_正确设置Scene与立绘()
    {
        var vm = new ImagePreviewViewModel();
        var images = new List<ImagePreviewInfo>
        {
            new("scene bg room", "背景", "C:/game/images/bg/room.png", "images/bg/room.png", "1920 × 1080", "1.2 MB"),
            new("show eileen happy", "立绘", "C:/game/images/eileen_happy.png", "images/eileen_happy.png", "800 × 1200", "450 KB")
        };
        var context = new RenPySceneContext("scene bg room", "bg room", images);

        vm.UpdateContext(context);

        Assert.True(vm.HasImages);
        Assert.Equal(2, vm.AvailableImages.Count);
        Assert.Equal("scene bg room", vm.SceneTag);
        Assert.NotNull(vm.SelectedImage);
        Assert.Equal("scene bg room", vm.SelectedImage.Tag);
    }

    [Fact]
    public void ImagePreviewViewModel_无图片时正确设置提示()
    {
        var vm = new ImagePreviewViewModel();
        var context = new RenPySceneContext("scene bg missing", "bg missing", []);

        vm.UpdateContext(context);

        Assert.False(vm.HasImages);
        Assert.Empty(vm.AvailableImages);
        Assert.Contains("未在项目中找到同名图片素材", vm.StatusHint);
    }

    private sealed class TestDocument(string id, string title, bool canClose = true)
        : PatchDocumentViewModel(id, title, canClose)
    {
        public void MarkDirty() => IsDirty = true;
    }

    private sealed class FakeConfirmationService : IConfirmationService
    {
        public bool ConfirmResult { get; set; } = true;
        public int ConfirmationCount { get; private set; }

        public bool Confirm(string title, string message, MessageBoxImage image = MessageBoxImage.Question)
        {
            ConfirmationCount++;
            return ConfirmResult;
        }

        public void ShowDiagnostics(string title, IEnumerable<Diagnostic> diagnostics) { }
        public void ShowMessage(string title, string message, MessageBoxImage image = MessageBoxImage.Information) { }
    }
}
