/*
 *  File:   DiscordClientWrapper.cs
 *  Author: Angelo Breuer
 *
 *  The MIT License (MIT)
 *
 *  Copyright (c) Angelo Breuer 2022
 *
 *  Permission is hereby granted, free of charge, to any person obtaining a copy
 *  of this software and associated documentation files (the "Software"), to deal
 *  in the Software without restriction, including without limitation the rights
 *  to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 *  copies of the Software, and to permit persons to whom the Software is
 *  furnished to do so, subject to the following conditions:
 *
 *  The above copyright notice and this permission notice shall be included in
 *  all copies or substantial portions of the Software.
 *
 *  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 *  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 *  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 *  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 *  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 *  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 *  THE SOFTWARE.
 */

namespace Lavalink4NET.DiscordNet;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Discord.WebSocket;
using Lavalink4NET.Events;

/// <summary>
///     A wrapper for the discord client from the "Discord.NET" discord client library. (https://github.com/discord-net/Discord.Net)
/// </summary>
public sealed class DiscordClientWrapper : IDiscordClientWrapper, IDisposable
{
    private static readonly MethodInfo _disconnectMethod = typeof(SocketGuild)
        .GetMethod("DisconnectAudioAsync", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly BaseSocketClient _baseSocketClient;
    private readonly int? _shardCount;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiscordClientWrapper"/> class.
    /// </summary>
    /// <param name="client">the sharded discord client</param>
    public DiscordClientWrapper(DiscordShardedClient client)
        : this(client as BaseSocketClient)
    {
        // _shardCount is null here, and is retrieved dynamically from the client.
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiscordClientWrapper"/> class.
    /// </summary>
    /// <param name="client">the sharded discord client</param>
    /// <param name="shards">the number of total shards</param>
    public DiscordClientWrapper(DiscordShardedClient client, int shards)
        : this(client as BaseSocketClient, shards)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiscordClientWrapper"/> class.
    /// </summary>
    /// <param name="client">the sharded discord client</param>
    public DiscordClientWrapper(DiscordSocketClient client)
        : this(client, 1)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiscordClientWrapper"/> class.
    /// </summary>
    /// <param name="baseSocketClient">the discord client</param>
    /// <param name="shards">the number of shards</param>
    /// <exception cref="ArgumentNullException">
    ///     thrown if the specified <paramref name="baseSocketClient"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     thrown if the specified shard count is less than 1.
    /// </exception>
    public DiscordClientWrapper(BaseSocketClient baseSocketClient, int shards)
        : this(baseSocketClient)
    {
        if (shards < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(shards), shards, "Shard count must be at least 1.");
        }

        _shardCount = shards;
    }

    private DiscordClientWrapper(BaseSocketClient baseSocketClient)
    {
        _baseSocketClient = baseSocketClient ?? throw new ArgumentNullException(nameof(baseSocketClient));
        _baseSocketClient.VoiceServerUpdated += OnVoiceServerUpdated;
        _baseSocketClient.UserVoiceStateUpdated += OnVoiceStateUpdated;
    }

    /// <summary>
    ///     An asynchronous event which is triggered when the voice server was updated.
    /// </summary>
    public event AsyncEventHandler<VoiceServer>? VoiceServerUpdated;

    /// <summary>
    ///     An asynchronous event which is triggered when a user voice state was updated.
    /// </summary>
    public event AsyncEventHandler<VoiceStateUpdateEventArgs>? VoiceStateUpdated;

    /// <summary>
    ///     Gets the current user snowflake identifier value.
    /// </summary>
    public ulong CurrentUserId
    {
        get
        {
            EnsureAvailable();
            return _baseSocketClient.CurrentUser.Id;
        }
    }

    /// <summary>
    ///     Gets the number of total shards the bot uses.
    /// </summary>
    public int ShardCount
    {
        get
        {
            EnsureAvailable();

            if (_shardCount.HasValue)
            {
                // shard count was given in constructor, or no sharding is used (-> 1)
                return _shardCount.Value;
            }

            // retrieve shard count from client
            Debug.Assert(_baseSocketClient is DiscordShardedClient);
            return ((DiscordShardedClient)_baseSocketClient).Shards.Count;
        }
    }

    /// <summary>
    ///     Disposes the wrapper and unregisters all events attached to the discord client.
    /// </summary>
    public void Dispose()
    {
        _baseSocketClient.VoiceServerUpdated -= OnVoiceServerUpdated;
        _baseSocketClient.UserVoiceStateUpdated -= OnVoiceStateUpdated;
    }

    /// <summary>
    ///     Gets the snowflake identifier values of the users in the voice channel specified by
    ///     <paramref name="voiceChannelId"/> (the snowflake identifier of the voice channel).
    /// </summary>
    /// <param name="guildId">the guild identifier snowflake where the channel is in</param>
    /// <param name="voiceChannelId">the snowflake identifier of the voice channel</param>
    /// <returns>
    ///     a task that represents the asynchronous operation
    ///     <para>the snowflake identifier values of the users in the voice channel</para>
    /// </returns>
    public Task<IEnumerable<ulong>> GetChannelUsersAsync(ulong guildId, ulong voiceChannelId)
    {
        var guild = _baseSocketClient.GetGuild(guildId);

        if (guild is null)
        {
            // It may be that the guild has been deleted while there was a player for it, return no users
            return Task.FromResult(Enumerable.Empty<ulong>());
        }

        var channel = guild.GetVoiceChannel(voiceChannelId);

        if (channel is null)
        {
            // It may be that the channel has been deleted while there was a player for it, return no users
            return Task.FromResult(Enumerable.Empty<ulong>());
        }

        var users = channel.ConnectedUsers
            .Where(x => !x.IsBot)
            .Select(s => s.Id);

        return Task.FromResult(users);
    }

    /// <summary>
    ///     Awaits the initialization of the discord client asynchronously.
    /// </summary>
    /// <returns>a task that represents the asynchronous operation</returns>
    public async Task InitializeAsync()
    {
        var startTime = DateTimeOffset.UtcNow;

        // await until current user arrived
        while (_baseSocketClient.CurrentUser is null)
        {
            await Task.Delay(10);

            // timeout exceeded
            if (DateTimeOffset.UtcNow - startTime > TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException("Waited 10 seconds for current user to arrive! Make sure you start " +
                    "the discord client, before initializing the discord wrapper!");
            }
        }
    }

    /// <summary>
    ///     Sends a voice channel state update asynchronously.
    /// </summary>
    /// <param name="guildId">the guild snowflake identifier</param>
    /// <param name="voiceChannelId">
    ///     the snowflake identifier of the voice channel to join (if <see langword="null"/> the
    ///     client should disconnect from the voice channel).
    /// </param>
    /// <param name="selfDeaf">a value indicating whether the bot user should be self deafened</param>
    /// <param name="selfMute">a value indicating whether the bot user should be self muted</param>
    /// <returns>a task that represents the asynchronous operation</returns>
    public Task SendVoiceUpdateAsync(ulong guildId, ulong? voiceChannelId, bool selfDeaf = false, bool selfMute = false)
    {
        var guild = _baseSocketClient.GetGuild(guildId)
            ?? throw new ArgumentException("Invalid or inaccessible guild: " + guildId, nameof(guildId));

        if (voiceChannelId.HasValue)
        {
            var channel = guild.GetVoiceChannel(voiceChannelId.Value)
                ?? throw new ArgumentException("Invalid or inaccessible voice channel: " + voiceChannelId, nameof(voiceChannelId));

            return channel.ConnectAsync(selfDeaf, selfMute, external: true);
        }

        return (Task)_disconnectMethod.Invoke(guild, new object[0]);
    }

    private Task OnVoiceServerUpdated(SocketVoiceServer voiceServer)
    {
        var args = new VoiceServer(voiceServer.Guild.Id, voiceServer.Token, voiceServer.Endpoint);
        return VoiceServerUpdated.InvokeAsync(this, args);
    }

    private Task OnVoiceStateUpdated(SocketUser user, SocketVoiceState oldSocketVoiceState, SocketVoiceState socketVoiceState)
    {
        var guildId = oldSocketVoiceState.VoiceChannel?.Guild?.Id ?? socketVoiceState.VoiceChannel.Guild.Id;

        // create voice state
        var voiceState = new VoiceState(
            voiceChannelId: socketVoiceState.VoiceChannel?.Id,
            guildId: guildId,
            voiceSessionId: socketVoiceState.VoiceSessionId);

        // invoke event
        return VoiceStateUpdated.InvokeAsync(this, new VoiceStateUpdateEventArgs(user.Id, voiceState));
    }

    private void EnsureAvailable()
    {
        var currentUserAvailable = _baseSocketClient.CurrentUser is not null;

        if (!currentUserAvailable)
        {
            throw new InvalidOperationException("The underlying discord client is not ready.");
        }

        var shardsAvailable = _shardCount is not null // shard count given
            || (_baseSocketClient is DiscordShardedClient shardedClient && shardedClient.Shards is not null);

        if (!shardsAvailable)
        {
            throw new InvalidOperationException("The underlying discord client is not ready.");
        }
    }
}
