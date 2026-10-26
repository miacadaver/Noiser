using Common;
using Common.Streaming;
using Soulseek;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Seeker.Services
{
    public partial class DownloadService
    {
        private readonly object requestGate = new object();
        private readonly Dictionary<(string Username, string Filename), int> pendingNormalRequests = new Dictionary<(string, string), int>();
        private readonly Dictionary<(string Username, string Filename), ProgressiveBuffer> streamingRequests = new Dictionary<(string, string), ProgressiveBuffer>();
        // Keep token provenance for this client lifetime: late progress events must not touch download history.
        private readonly ConcurrentDictionary<(ISoulseekClient Client, int Token), byte> streamingTokens = new ConcurrentDictionary<(ISoulseekClient, int), byte>();

        public bool IsStreamingTransfer(object sender, Transfer transfer) =>
            transfer.Direction == TransferDirection.Download && sender is ISoulseekClient client && streamingTokens.ContainsKey((client, transfer.Token));

        public bool IsStreamingFile(string username, string filename)
        {
            lock (requestGate) return streamingRequests.ContainsKey((username, filename));
        }

        public void ReleaseStream(string username, string filename, ProgressiveBuffer buffer)
        {
            lock (requestGate)
            {
                var key = (username, filename);
                if (streamingRequests.TryGetValue(key, out var owner) && ReferenceEquals(owner, buffer))
                    streamingRequests.Remove(key);
            }
        }

        /// <summary>
        /// A temporary download uses the authenticated client, peer negotiation, resume offsets and speed governor.
        /// It owns no persisted transfer row and never enters the permanent-file completion continuation.
        /// </summary>
        public async Task StreamAsync(string username, FullFileInfo file, ProgressiveBuffer buffer,
            Action<TransferStates> stateChanged, CancellationToken cancellationToken, bool retainUntilReleased = false,
            Action? reconnecting = null)
        {
            var key = (username, file.FullFileName);
            // Inspect the transfer list outside requestGate; UI callers can hold list locks when retrying.
            var existing = TransferItems.TransferItemManagerDL?.GetTransferItemWithIndexFromAll(file.FullFileName, username, out _);
            lock (requestGate)
            {
                if (username == PreferencesState.Username) throw new InvalidOperationException("Cannot stream from yourself.");
                if (pendingNormalRequests.ContainsKey(key))
                    throw new InvalidOperationException("This file is already being added to Downloads.");
                foreach (var request in activeRequests.Values)
                    if (request.username == username && request.fullFilename == file.FullFileName)
                        throw new InvalidOperationException("This file is already downloading. Open it from Transfers when it finishes.");
                if (existing?.InProcessing == true)
                    throw new InvalidOperationException("This download is finishing. Open it from Transfers when it finishes.");
                if (streamingRequests.ContainsKey(key)) throw new InvalidOperationException("This file is already streaming.");
                streamingRequests.Add(key, buffer);
            }
            bool succeeded = false;
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var client = soulseekClientFactory();
                    int token = client.GetNextToken();
                    streamingTokens.TryAdd((client, token), 0);
                    try
                    {
                        await client.DownloadAsync(username, file.FullFileName,
                            () => Task.FromResult(buffer.Writer), size: file.Size,
                            startOffset: buffer.AvailableLength, token: token,
                            options: new TransferOptions(
                                governor: SpeedLimitHelper.OurDownloadGovernor,
                                stateChanged: args => stateChanged(args.Transfer.State),
                                seekOutputStreamAutomatically: false,
                                disposeOutputStreamOnCompletion: false),
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        buffer.Complete();
                        succeeded = true;
                        return;
                    }
                    catch (Exception exception) when (attempt == 0 && !cancellationToken.IsCancellationRequested && CanRetryStream(exception))
                    {
                        // Retain every committed byte; a reconnect may put us back in the peer's queue.
                        logger.Debug("Resuming MP3 stream: " + exception.Message);
                        stateChanged(TransferStates.Queued);
                        reconnecting?.Invoke();
                        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception exception)
            {
                buffer.Fail(exception);
                throw;
            }
            finally
            {
                if (!succeeded || !retainUntilReleased) ReleaseStream(username, file.FullFileName, buffer);
                SpeedLimitHelper.RemoveDownloadUser(username);
            }
        }

        private static bool CanRetryStream(Exception exception)
        {
            var kind = DownloadFailureClassifier.Classify(exception);
            return kind == DownloadFailureKind.TimedOut || kind == DownloadFailureKind.CannotConnect
                || kind == DownloadFailureKind.ReportedFailed || kind == DownloadFailureKind.ConnectionClosed;
        }
    }
}
