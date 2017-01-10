﻿namespace UB3RB0T
{
    using Discord;
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;

    public class AudioManager : IDisposable
    {
        internal static readonly SemaphoreSlim streamLock  = new SemaphoreSlim(1, 1);

        private ConcurrentDictionary<ulong, AudioInstance> audioInstances = new ConcurrentDictionary<ulong, AudioInstance>();

        public async Task JoinAudioAsync(IVoiceChannel voiceChannel)
        {
            if (!audioInstances.TryGetValue(voiceChannel.GuildId, out AudioInstance audioInstance))
            {
                audioInstance = new AudioInstance
                {
                    GuildId = voiceChannel.GuildId,
                    AudioClient = await voiceChannel.ConnectAsync().ConfigureAwait(false)
                };
                audioInstance.Stream = audioInstance.AudioClient.CreatePCMStream(2880, bitrate: voiceChannel.Bitrate);
                audioInstances.TryAdd(voiceChannel.GuildId, audioInstance);
            }

            if (audioInstance.AudioClient.ConnectionState == ConnectionState.Connected)
            {
                await this.SendAudioAsync(audioInstance, "hello.mp3");
            }
            else
            {
                audioInstance.AudioClient.Connected += async () =>
                {
                    await this.SendAudioAsync(audioInstance, "hello.mp3");
                    await Task.CompletedTask;
                };
                audioInstance.AudioClient.Disconnected += async (Exception ex) =>
                {
                    await this.LeaveAudioAsync(voiceChannel.GuildId);
                    Console.WriteLine(ex);
                };
            }
        }

        public async Task LeaveAllAudioAsync()
        {
            foreach (var key in audioInstances.Keys)
            {
                await this.LeaveAudioAsync(key);
            }
        }

        public async Task LeaveAudioAsync(IGuildChannel guildChannel)
        {
            await this.LeaveAudioAsync(guildChannel.GuildId);
        }

        public async Task LeaveAudioAsync(ulong guildId)
        {
            if (audioInstances.TryRemove(guildId, out AudioInstance audioInstance))
            {
                // say our goodbyes
                try
                {
                    await this.SendAudioAsync(audioInstance, "goodbye.mp3");
                    await Task.Delay(1000);
                }
                catch (Exception ex)
                {
                    // TODO: proper logging
                    Console.WriteLine(ex);
                }

                await streamLock.WaitAsync();
                audioInstance.Stream.Dispose();
                audioInstance.Stream = null;
                streamLock.Release();

                try
                {
                    await audioInstance.AudioClient.DisconnectAsync();
                }
                catch (Exception ex)
                {
                    // TODO: proper logging
                    Console.WriteLine(ex);
                }

                audioInstance.AudioClient.Dispose();
            }
        }

        public async Task SendAudioAsync(IVoiceChannel voiceChannel, string filename)
        {
            if (voiceChannel is IGuildChannel guildChannel)
            {
                var botGuildUser = await guildChannel.Guild.GetCurrentUserAsync();

                if (voiceChannel != null && botGuildUser.VoiceChannel == voiceChannel)
                {
                    if (audioInstances.TryGetValue(voiceChannel.GuildId, out AudioInstance audioInstance))
                    {
                        await this.SendAudioAsync(audioInstance, filename);
                    }
                }
            }
        }

        public async Task SendAudioAsync(AudioInstance audioInstance, string filename)
        {
            Task.Run(async () =>
            {
                try
                {
                    await this.SendAudioAsyncInternalAsync(audioInstance, filename);
                }
                catch (Exception ex)
                {
                    // TODO: proper logging
                    Console.WriteLine(ex);
                }
            }).Forget();

            await Task.CompletedTask;
        }

        private async Task SendAudioAsyncInternalAsync(AudioInstance audioInstance, string filename)
        {
            Console.WriteLine($"[audio] [{filename}] sendaudio begin");
            var filePath = PhrasesConfig.Instance.VoiceFilePath;
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "c:\\audio\\ffmpeg",
                Arguments = $"-i {filePath}{filename} -f s16le -ar 48000 -ac 2 pipe:1 -loglevel error",
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });

            await streamLock.WaitAsync();
            Console.WriteLine($"[audio] [{filename}] inside audio lock");
            try
            {
                if (audioInstance.Stream != null)
                {
                    await p.StandardOutput.BaseStream.CopyToAsync(audioInstance.Stream);
                    Console.WriteLine($"[audio] [{filename}] stream copied");
                    p.WaitForExit();
                    Console.WriteLine($"[audio] [{filename}] process exit");
                    var flushTask = audioInstance.Stream.FlushAsync();
                    var timeoutTask = Task.Delay(10000);
                    if (await Task.WhenAny(flushTask, timeoutTask) == timeoutTask)
                    {
                        Console.WriteLine($"[audio] [{filename}] timeout occurred");
                        throw new TimeoutException();
                    }
                    Console.WriteLine($"[audio] [{filename}] stream flushed");
                }
                else
                {
                    Console.WriteLine($"[audio] [{filename}] stream was null, skipped.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                audioInstance.Stream.Dispose();
                audioInstance.AudioClient.Dispose();
                audioInstances.TryRemove(audioInstance.GuildId, out AudioInstance oldInstance);
            }
            finally
            {
                streamLock.Release();
                Console.WriteLine($"[audio] [{filename}] lock released");
            }

            Console.WriteLine($"[audio] [{filename}] sendaudio end");
        }

        public void Dispose() => Dispose(true);

        public void Dispose(bool isDisposing)
        {
            foreach (var kvp in this.audioInstances)
            {
                kvp.Value.Stream.Dispose();
                kvp.Value.AudioClient.Dispose();
            }
            this.audioInstances.Clear();
        }
    }
}
