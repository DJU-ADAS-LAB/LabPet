using System.Reflection;
using LabPet.Client.Social;
using LabPet.Shared.Models;
using LabPet.Shared.Transport;
using Xunit;

namespace LabPet.Tests;

public sealed class SnowballCooldownTests
{
    [Fact]
    public async Task DelayedResponseDoesNotBlockRetryOrOverwriteNewerSuccess()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var transport = DispatchProxy.Create<ISocialTransport, ControlledTransport>();
            var control = (ControlledTransport)(object)transport;
            using var service = new SnowballInteractionService(transport);
            var friend = new FriendEntry { PetId = Guid.NewGuid().ToString(), PairSecretBase64 = "test-secret" };
            var action = service.ForFriend(friend);
            Assert.Same(action, service.ForFriend(friend));
            var first = action.Command.ExecuteAsync(null);
            Assert.Single(control.Requests);
            Assert.False(action.Command.CanExecute(null));
            await action.Command.ExecuteAsync(null);
            Assert.Single(control.Requests);

            var other = service.ForFriend(new FriendEntry
            {
                PetId = Guid.NewGuid().ToString(), PairSecretBase64 = "test-secret"
            });
            Assert.True(other.Command.CanExecute(null));
            var independent = other.Command.ExecuteAsync(null);
            Assert.Equal(2, control.Requests.Count);
            control.Requests[1].Completion.SetResult(new TransportSendResult(true));
            await independent;

            var enabled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(object? sender, EventArgs e)
            {
                if (action.Command.CanExecute(null)) enabled.TrySetResult();
            }
            action.Command.CanExecuteChanged += OnChanged;
            try
            {
                // 전송은 완료하지 않고 실제 만료 알림만 기다린다.
                await enabled.Task.WaitAsync(TimeSpan.FromSeconds(4));
                Assert.False(first.IsCompleted);
                Assert.True(action.Command.CanExecute(null));
                var retry = action.Command.ExecuteAsync(null);
                Assert.Equal(3, control.Requests.Count);
                Assert.NotEqual(control.Requests[0].Id, control.Requests[2].Id);
                control.Requests[2].Completion.SetResult(new TransportSendResult(true));
                await retry;
                control.Requests[0].Completion.SetResult(TransportSendResult.Fail());
                await first;
                Assert.DoesNotContain("실패", action.Tooltip);
                Assert.False(action.Command.CanExecute(null));
            }
            finally { action.Command.CanExecuteChanged -= OnChanged; }
        });
    }

    // 테스트에서만 응답 순서를 제어하며 실제 통신 계층은 변경하지 않는다.
    public class ControlledTransport : DispatchProxy
    {
        public List<(Guid Id, TaskCompletionSource<TransportSendResult> Completion)> Requests { get; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case "GetSnowballPeerStatus": return SnowballPeerStatus.Available;
                case "GetDiscoveredPeers": return Array.Empty<PeerSnapshot>();
                case "SendSnowballDetailedAsync":
                    var completion = new TaskCompletionSource<TransportSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Requests.Add(((Guid)args![1]!, completion));
                    return completion.Task;
                default:
                    if (method?.Name.StartsWith("add_", StringComparison.Ordinal) == true
                        || method?.Name.StartsWith("remove_", StringComparison.Ordinal) == true) return null;
                    throw new NotSupportedException(method?.Name);
            }
        }
    }
}
