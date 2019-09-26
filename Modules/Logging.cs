﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Botwinder.core;
using Botwinder.entities;
using Discord;
using Discord.Net;
using Discord.Rest;
using Discord.WebSocket;

using guid = System.UInt64;

namespace Botwinder.modules
{
	public class Logging: IModule
	{
		private class Message
		{
			public MessageType DesiredType;
			public Embed LogEmbed;
			public string LogString;
			public SocketTextChannel Channel;

			public async Task Send()
			{
				if( this.DesiredType == MessageType.Embed )
					await this.Channel.SendMessageAsync(embed: this.LogEmbed);
				else if( this.DesiredType == MessageType.String )
					await this.Channel.SendMessageSafe(this.LogString);
			}
		}

		private enum MessageType
		{
			Embed,
			String
		}

		private BotwinderClient Client;

		private readonly List<guid> RecentlyBannedUserIDs = new List<guid>();
		private readonly List<guid> RecentlyUnbannedUserIDs = new List<guid>();

		private const int MessageQueueThreshold = 5;
		private const int MessageQueueFileThreshold = 50;
		private readonly List<Message> MessageQueue= new List<Message>();
		private readonly TimeSpan UpdateDelay = TimeSpan.FromSeconds(MessageQueueThreshold);
		private Task UpdateTask;
		private CancellationTokenSource UpdateCancel;
		private readonly SemaphoreSlim MessageQueueLock = new SemaphoreSlim(0, 1);

		private readonly Color AntispamColor = new Color(255, 0, 255);
		private readonly Color AntispamLightColor = new Color(255, 0, 206);


		public Func<Exception, string, guid, Task> HandleException{ get; set; }
		public bool DoUpdate{ get; set; } = false;

		public List<Command> Init(IBotwinderClient iClient)
		{
			this.Client = iClient as BotwinderClient;

			this.Client.Events.UserJoined += OnUserJoined;
			this.Client.Events.UserLeft += OnUserLeft;
			this.Client.Events.UserVoiceStateUpdated += OnUserVoice;
			this.Client.Events.MessageDeleted += OnMessageDeleted;
			this.Client.Events.MessageUpdated += OnMessageUpdated;
			this.Client.Events.MessageReceived += OnMessageReceived;
			this.Client.Events.UserBanned += OnUserBanned;
			this.Client.Events.UserUnbanned += OnUserUnbanned;
			this.Client.Events.LogWarning += LogWarning;
			this.Client.Events.LogBan += LogBan;
			this.Client.Events.LogUnban += LogUnban;
			this.Client.Events.LogKick += LogKick;
			this.Client.Events.LogMute += LogMute;
			this.Client.Events.LogUnmute += LogUnmute;
			this.Client.Events.LogMutedChannel += LogMutedChannel;
			this.Client.Events.LogUnmutedChannel += LogUnmutedChannel;

			this.Client.Events.LogPublicRoleJoin += LogPublicRoleJoin;
			this.Client.Events.LogPublicRoleLeave += LogPublicRoleLeave;
			this.Client.Events.LogPromote += LogPromote;
			this.Client.Events.LogDemote += LogDemote;

			if( this.UpdateTask == null )
			{
				this.UpdateCancel = new CancellationTokenSource();
				this.UpdateTask = Task.Run(QueueUpdate, this.UpdateCancel.Token);
			}

			return new List<Command>();
		}

		private async Task QueueUpdate()
		{
			while( !this.UpdateCancel.IsCancellationRequested )
			{
				DateTime frameTime = DateTime.UtcNow;
				guid serverId = 0;

				while( this.MessageQueue.Any() )
				{
					await this.MessageQueueLock.WaitAsync();
					SocketTextChannel channel = this.MessageQueue.First().Channel;
					if( this.Client.GlobalConfig.LogDebug )
						Console.WriteLine($"Logging.Queue: handle messages for channel {channel.Id}");
					try
					{
						StringBuilder logText = new StringBuilder();
						serverId = channel.Guild.Id;
						List<Message> channelQueue = this.MessageQueue.Where(m => m.Channel.Id == channel.Id).ToList();
						if( channelQueue.Count > MessageQueueThreshold )
						{
							//Group the messages.
							bool sendAsFile = channelQueue.Count > MessageQueueFileThreshold;
							if( this.Client.GlobalConfig.LogDebug )
								Console.WriteLine("Logging.Queue: group the messages");
							foreach( Message logMsg in channelQueue )
							{
								if( !sendAsFile && logText.Length + logMsg.LogString.Length >= GlobalConfig.MessageCharacterLimit )
								{
									await channel.SendMessageSafe(logText.ToString());
									logText.Clear();
								}

								logText.AppendLine(logMsg.LogString);
							}
							if( this.Client.GlobalConfig.LogDebug )
								Console.WriteLine("Logging.Queue: group the messages DONE");

							if( sendAsFile )
							{
								if( this.Client.GlobalConfig.LogDebug )
									Console.WriteLine("Logging.Queue: send as file");
								using( Stream stream = new MemoryStream() )
								{
									using( StreamWriter writer = new StreamWriter(stream) )
										writer.Write(logText.ToString());
									string timestamp = Utils.GetTimestamp();
									await channel.SendFileAsync(stream, timestamp, $"`{timestamp}` - large number of log messages.");
								}
								if( this.Client.GlobalConfig.LogDebug )
									Console.WriteLine("Logging.Queue: send as file DONE");
							}
							else if( logText.Length > 0 )
								await channel.SendMessageSafe(logText.ToString());
						}
						else
						{
							//Send the messages one by one.
							if( this.Client.GlobalConfig.LogDebug )
								Console.WriteLine("Logging.Queue: send one by one");
							foreach( Message logMsg in channelQueue )
							{
								if( this.Client.GlobalConfig.LogDebug && !string.IsNullOrWhiteSpace(logMsg.LogString) )
									Console.WriteLine(logMsg.LogString);
								await logMsg.Send();
							}
							if( this.Client.GlobalConfig.LogDebug )
								Console.WriteLine("Logging.Queue: send one by one DONE");
						}
					}
					catch( HttpException exception )
					{
						if( this.Client.Servers.ContainsKey(serverId) )
							this.Client.Servers[serverId]?.HandleHttpException(exception);
					}
					catch( Exception exception )
					{
						await this.HandleException(exception, "LogMessageQueue", serverId);
					}

					this.MessageQueue.RemoveAll(m => m.Channel.Id == channel.Id);
					this.MessageQueueLock.Release();
				}

				TimeSpan deltaTime = DateTime.UtcNow - frameTime;
				if( this.Client.GlobalConfig.LogDebug )
					Console.WriteLine($"BotwinderClient: LogQueueUpdate loop took: {deltaTime.TotalMilliseconds} ms");
				await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1000, (this.UpdateDelay - deltaTime).TotalMilliseconds)));
			}
		}

		private async Task OnUserJoined(SocketGuildUser user)
		{
			Server server;
			if( !this.Client.Servers.ContainsKey(user.Guild.Id) ||
				(server = this.Client.Servers[user.Guild.Id]) == null )
				return;

			try
			{
				SocketTextChannel channel = server.Guild.GetTextChannel(server.Config.ActivityChannelId);
				if( server.Config.LogJoin && channel != null && !string.IsNullOrWhiteSpace(server.Config.LogMessageJoin) )
				{
					if( server.Config.AntispamInvites && server.Config.AntispamInvitesBan && (this.Client.RegexDiscordInvites?.Match(user.Username).Success ?? false) )
						return;

					if( server.Config.AntispamUsername && this.Client.RegexDiscordInvites != null && (this.Client.RegexDiscordInvites.Match(user.Username).Success || this.Client.RegexShortLinks.Match(user.Username).Success || this.Client.RegexYoutubeLinks.Match(user.Username).Success || this.Client.RegexTwitchLinks.Match(user.Username).Success || this.Client.RegexHitboxLinks.Match(user.Username).Success || this.Client.RegexBeamLinks.Match(user.Username).Success || this.Client.RegexImgurOrGifLinks.Match(user.Username).Success || this.Client.RegexTwitterLinks.Match(user.Username).Success) )
						return;

					string joinMessage = string.Format(server.Config.LogMessageJoin, user.GetUsername());
					DateTime accountCreated = Utils.GetTimeFromId(user.Id);
					Message msg = new Message(){
						Channel = channel,
						DesiredType = (server.Config.ActivityChannelEmbeds && joinMessage.Length < 255) ? MessageType.Embed : MessageType.String,
						LogEmbed = GetLogSmolEmbed(new Color(server.Config.ActivityChannelColor),
							joinMessage,
							user.GetAvatarUrl(), $"UserId: {user.Id}",
							"Account created: " + Utils.GetTimestamp(accountCreated), accountCreated),
						LogString = string.Format((server.Config.LogTimestampJoin ? $"`{Utils.GetTimestamp()}`: " : "") + server.Config.LogMessageJoin,
								server.Config.LogMentionJoin ? $"<@{user.Id}>" : $"**{user.GetNickname()}**")
							.Replace("@everyone", "@-everyone").Replace("@here", "@-here")
					};
					await this.MessageQueueLock.WaitAsync();
					this.MessageQueue.Add(msg);
					this.MessageQueueLock.Release();
				}
			}
			catch( HttpException exception )
			{
				await server.HandleHttpException(exception);
			}
			catch(Exception exception)
			{
				await this.HandleException(exception, "OnUserJoined", server.Id);
			}
		}

		private async Task OnUserLeft(SocketGuildUser user)
		{
			Server server;
			if( !this.Client.Servers.ContainsKey(user.Guild.Id) ||
			    (server = this.Client.Servers[user.Guild.Id]) == null||
			    this.RecentlyBannedUserIDs.Contains(user.Id) )
				return;

			try
			{
				SocketTextChannel channel = server.Guild.GetTextChannel(server.Config.ActivityChannelId);
				if( server.Config.LogLeave && channel != null && !string.IsNullOrWhiteSpace(server.Config.LogMessageLeave) )
				{
					string leaveMessage = string.Format(server.Config.LogMessageLeave, user.GetUsername());
					DateTime accountCreated = Utils.GetTimeFromId(user.Id);

					Message msg = new Message(){
						Channel = channel,
						DesiredType = (server.Config.ActivityChannelEmbeds && leaveMessage.Length < 255) ? MessageType.Embed : MessageType.String,
						LogEmbed = GetLogSmolEmbed(new Color(server.Config.ActivityChannelColor),
							leaveMessage,
							user.GetAvatarUrl(), $"UserId: {user.Id}",
							"Account created: " + Utils.GetTimestamp(accountCreated), accountCreated),
						LogString = string.Format((server.Config.LogTimestampLeave ? $"`{Utils.GetTimestamp()}`: " : "") + server.Config.LogMessageLeave,
								server.Config.LogMentionLeave ? $"<@{user.Id}>" : $"**{user.GetNickname()}**")
							.Replace("@everyone", "@-everyone").Replace("@here", "@-here")
					};
					await this.MessageQueueLock.WaitAsync();
					this.MessageQueue.Add(msg);
					this.MessageQueueLock.Release();
				}
			}
			catch( HttpException exception )
			{
				await server.HandleHttpException(exception);
			}
			catch(Exception exception)
			{
				await this.HandleException(exception, "OnUserLeft", server.Id);
			}

		}

		private async Task OnUserVoice(SocketUser u, SocketVoiceState originalState, SocketVoiceState newState)
		{
			Server server;
			guid id = newState.VoiceChannel?.Guild.Id ?? originalState.VoiceChannel?.Guild.Id ?? 0;
			if( id == 0 ||
			    !this.Client.Servers.ContainsKey(id) ||
			    (server = this.Client.Servers[id]) == null ||
			    !(u is SocketGuildUser user) ||
			    originalState.VoiceChannel == newState.VoiceChannel )
				return;

			try
			{
				SocketTextChannel channel;
				if( server.Config.VoiceChannelId != 0 &&
				    (channel = server.Guild.GetTextChannel(server.Config.VoiceChannelId)) != null )
				{
					if( originalState.VoiceChannel == null && newState.VoiceChannel == null )
						throw new ArgumentNullException("Logging.VoiceState.VoiceChannel(s) are null.");

					int change = originalState.VoiceChannel == null ? 1 :
						newState.VoiceChannel == null ? -1 : 0;

					string message = "";
					Embed embed = null;
					switch( change )
					{
						case -1:
							message = $"`{Utils.GetTimestamp()}`:  **{user.GetNickname()}** left the `{originalState.VoiceChannel.Name}` voice channel.";
							embed = GetLogSmolEmbed(new Color(server.Config.VoiceChannelColor),
								user.GetUsername() + " left the voice channel:",
								user.GetAvatarUrl(), $"{originalState.VoiceChannel.Name}",
								Utils.GetTimestamp(DateTime.UtcNow), DateTime.UtcNow);
							break;
						case 1:
							message = $"`{Utils.GetTimestamp()}`:  **{user.GetNickname()}** joined the `{newState.VoiceChannel.Name}` voice channel.";
							embed = GetLogSmolEmbed(new Color(server.Config.VoiceChannelColor),
								user.GetUsername() + " joined the voice channel:",
								user.GetAvatarUrl(), $"{newState.VoiceChannel.Name}",
								Utils.GetTimestamp(DateTime.UtcNow), DateTime.UtcNow);
							break;
						case 0:
							message = $"`{Utils.GetTimestamp()}`:  **{user.GetNickname()}** switched from the `{originalState.VoiceChannel.Name}` voice channel, to the `{newState.VoiceChannel.Name}` voice channel.";
							embed = GetLogSmolEmbed(new Color(server.Config.VoiceChannelColor),
								user.GetUsername() + " switched voice channels:",
								user.GetAvatarUrl(), $"From {originalState.VoiceChannel.Name} to {newState.VoiceChannel.Name}",
								Utils.GetTimestamp(DateTime.UtcNow), DateTime.UtcNow);
							break;
						default:
							throw new ArgumentOutOfRangeException();
					}

					Message msg = new Message(){
						Channel = channel,
						DesiredType = (server.Config.VoiceChannelEmbeds) ? MessageType.Embed : MessageType.String,
						LogEmbed = embed,
						LogString = message
					};
					await this.MessageQueueLock.WaitAsync();
					this.MessageQueue.Add(msg);
					this.MessageQueueLock.Release();
				}
			}
			catch( HttpException exception )
			{
				await server.HandleHttpException(exception);
			}
			catch(Exception exception)
			{
				await this.HandleException(exception, "OnUserVoice", server.Id);
			}

		}

		private async Task OnMessageDeleted(SocketMessage message, ISocketMessageChannel c)
		{
			if( !this.Client.GlobalConfig.ModuleUpdateEnabled )
				return;

			if( !(c is SocketTextChannel channel) )
				return;

			Server server;
			if( !this.Client.Servers.ContainsKey(channel.Guild.Id) ||
			    (server = this.Client.Servers[channel.Guild.Id]) == null ||
			    server.Config.IgnoreBots && message.Author.IsBot ||
			    !(message.Author is SocketGuildUser user) )
				return;

			try
			{
				SocketTextChannel logChannel;
				if( server.Config.LogDeletedMessages && (logChannel = server.Guild.GetTextChannel(server.Config.LogChannelId)) != null && !(
					    (this.Client.ClearedMessageIDs.ContainsKey(server.Id) && this.Client.ClearedMessageIDs[server.Id].Contains(message.Id)) ||
					    server.IgnoredChannels.Contains(channel.Id) ||
					    server.Roles.Where(r => r.Value.LoggingIgnored).Any(r => user.Roles.Any(role => role.Id == r.Value.RoleId))) )
				{
					StringBuilder attachment = new StringBuilder();
					if( message.Attachments != null && message.Attachments.Any() )
						foreach( Attachment a in message.Attachments )
							if( !string.IsNullOrWhiteSpace(a.Url) )
								attachment.AppendLine(a.Url);

					MessageDeleteAuditLogData auditData = null;
					RestAuditLogEntry auditEntry = null;
					if( server.Guild.CurrentUser.GuildPermissions.ViewAuditLog )
					{
						await Task.Delay(500);
						try
						{
							auditEntry = await server.Guild.GetAuditLogsAsync(10)?.Flatten()?.FirstOrDefault(e => e != null && e.Action == ActionType.MessageDeleted && (auditData = e.Data as MessageDeleteAuditLogData) != null && auditData.ChannelId == c.Id && (Utils.GetTimeFromId(e.Id) + TimeSpan.FromMinutes(5)) > DateTime.UtcNow);
							//One huge line because black magic from .NET Core?
						}
						catch( Exception ) { }
					}

					bool byAntispam = this.Client.AntispamMessageIDs.Contains(message.Id);
					string title = "Message Deleted" + (byAntispam ? " by Antispam" : auditEntry != null ? (" by " + auditEntry.User.GetUsername()) : "");
					Color color = byAntispam ? this.AntispamLightColor : new Color(server.Config.LogMessagesColor);
					Message msg = new Message(){
						Channel = channel,
						DesiredType = (server.Config.LogChannelEmbeds) ? MessageType.Embed : MessageType.String,
						LogEmbed = GetLogEmbed(color, user?.GetAvatarUrl(), title, "in #" + channel.Name,
							message.Author.GetUsername(), message.Author.Id.ToString(),
							message.Id,
							"Message", message.Content.Replace("@everyone", "@-everyone").Replace("@here", "@-here"),
							message.Attachments.Any() ? "Files" : "", attachment.ToString()),
						LogString = GetLogMessage(title, "#" + channel.Name,
							message.Author.GetUsername(), message.Author.Id.ToString(),
							message.Id,
							"Message", message.Content.Replace("@everyone", "@-everyone").Replace("@here", "@-here"),
							message.Attachments.Any() ? "Files" : "", attachment.ToString())
					};
					await this.MessageQueueLock.WaitAsync();
					this.MessageQueue.Add(msg);
					this.MessageQueueLock.Release();
				}
			}
			catch( HttpException exception )
			{
				await server.HandleHttpException(exception);
			}
			catch(Exception exception)
			{
				await this.HandleException(exception, "OnMessageDeleted", server.Id);
			}
		}

		private async Task OnMessageUpdated(SocketMessage originalMessage, SocketMessage updatedMessage, ISocketMessageChannel c)
		{
			if( !this.Client.GlobalConfig.ModuleUpdateEnabled )
				return;

			if( originalMessage == null || updatedMessage == null ||
			    originalMessage.Content == updatedMessage.Content ||
			    !(c is SocketTextChannel channel) )
				return;

			Server server;
			if( !this.Client.Servers.ContainsKey(channel.Guild.Id) ||
			    (server = this.Client.Servers[channel.Guild.Id]) == null ||
			    server.Config.IgnoreBots && updatedMessage.Author.IsBot ||
			    !(updatedMessage.Author is SocketGuildUser user) )
				return;

			try
			{
				SocketTextChannel logChannel;
				if( server.Config.LogEditedMessages && (logChannel = server.Guild.GetTextChannel(server.Config.LogChannelId)) != null && !(
					    server.IgnoredChannels.Contains(channel.Id) ||
					    server.Roles.Where(r => r.Value.LoggingIgnored).Any(r => user.Roles.Any(role => role.Id == r.Value.RoleId))) )
				{
					Message msg = new Message(){
						Channel = channel,
						DesiredType = (server.Config.LogChannelEmbeds) ? MessageType.Embed : MessageType.String,
						LogEmbed = GetLogEmbed(new Color(server.Config.LogMessagesColor), user?.GetAvatarUrl(),
							"Message Edited", "in #" + channel.Name,
							updatedMessage.Author.GetUsername(), updatedMessage.Author.Id.ToString(),
							updatedMessage.Id,
							"Before", originalMessage.Content.Replace("@everyone", "@-everyone").Replace("@here", "@-here"),
							"After", updatedMessage.Content.Replace("@everyone", "@-everyone").Replace("@here", "@-here")),
						LogString = GetLogMessage("Message Edited", "#" + channel.Name,
							updatedMessage.Author.GetUsername(), updatedMessage.Author.Id.ToString(),
							updatedMessage.Id,
							"Before", originalMessage.Content.Replace("@everyone", "@-everyone").Replace("@here", "@-here"),
							"After", updatedMessage.Content.Replace("@everyone", "@-everyone").Replace("@here", "@-here"))
					};
				}
			}
			catch( HttpException exception )
			{
				await server.HandleHttpException(exception);
			}
			catch(Exception exception)
			{
				await this.HandleException(exception, "OnMessageUpdated", server.Id);
			}
		}

		private async Task OnMessageReceived(SocketMessage message)
		{
			if( !this.Client.GlobalConfig.ModuleUpdateEnabled )
				return;

			if( message == null || !(message.Channel is SocketTextChannel channel) )
				return;

			Server server;
			if( !this.Client.Servers.ContainsKey(channel.Guild.Id) || (server = this.Client.Servers[channel.Guild.Id]) == null ||
			    server.Config.IgnoreBots && message.Author.IsBot || !(message.Author is SocketGuildUser user) )
				return;

			try
			{
				SocketTextChannel logChannel;
				if( server.Config.AlertChannelId != 0 && (logChannel = server.Guild.GetTextChannel(server.Config.AlertChannelId)) != null &&
				    server.Config.AlertChannelId != message.Channel.Id && server.AlertRegex != null && server.AlertRegex.IsMatch(message.Content) )
				{
					Message msg = new Message(){
						Channel = channel,
						DesiredType = MessageType.Embed,
						LogEmbed = GetLogEmbed(new Color(server.Config.AlertChannelColor), user?.GetAvatarUrl(),
							"Alert triggered", $"in [#{channel.Name}](https://discordapp.com/channels/{server.Id}/{channel.Id}/{message.Id})",
							message.Author.GetUsername(), message.Author.Id.ToString(),
							message.Id,
							"Content", message.Content.Replace("@everyone", "@-everyone").Replace("@here", "@-here")),
						LogString = ""
					};
					await this.MessageQueueLock.WaitAsync();
					this.MessageQueue.Add(msg);
					this.MessageQueueLock.Release();
				}
			}
			catch( HttpException exception )
			{
				await server.HandleHttpException(exception);
			}
			catch(Exception exception)
			{
				await this.HandleException(exception, "OnMessageReceived", server.Id);
			}
		}


		private async Task OnUserBanned(SocketUser user, SocketGuild guild)
		{
			await Task.Delay(300); //Ensure that this event gets triggered after our own event.

			Server server;
			if( !this.Client.Servers.ContainsKey(guild.Id) || (server = this.Client.Servers[guild.Id]) == null )
				return;

			BanAuditLogData auditData = null;
			RestAuditLogEntry auditEntry = null;
			if( guild.CurrentUser.GuildPermissions.ViewAuditLog )
			{
				await Task.Delay(300);
				try
				{
					auditEntry = await guild.GetAuditLogsAsync(10)?.Flatten()?.FirstOrDefault(e => e != null && e.Action == ActionType.Ban && (auditData = e.Data as BanAuditLogData) != null && auditData.Target.Id == user.Id);
					//One huge line because black magic from .NET Core?
				}
				catch(Exception) { }
			}

			string reason = "unknown";
			RestBan ban = await server.Guild.GetBanAsync(user);
			if( ban != null )
			{
				reason = ban.Reason;
				await this.Client.Events.AddBan(guild.Id, user.Id, TimeSpan.Zero, reason);
			}
			if( !this.RecentlyBannedUserIDs.Contains(user.Id) )
				await LogBan(server, user.GetUsername(), user.Id, reason, "permanently", auditEntry?.User as SocketGuildUser);
		}

		private async Task OnUserUnbanned(SocketUser user, SocketGuild guild)
		{
			await Task.Delay(300); //Ensure that this event gets triggered after our own event.

			Server server;
			if( !this.Client.Servers.ContainsKey(guild.Id) || (server = this.Client.Servers[guild.Id]) == null ||
			    this.RecentlyUnbannedUserIDs.Contains(user.Id) )
				return;

			await LogUnban(server, user.GetUsername(), user.Id, null);
		}

		private async Task LogWarning(Server server, List<string> userNames, List<guid> userIds, string warning, SocketGuildUser issuedBy)
		{
			try
			{
				SocketTextChannel logChannel;
				if( !server.Config.LogWarnings || (logChannel = server.Guild.GetTextChannel(server.Config.ModChannelId)) == null )
					return;

				Color color = new Color(server.Config.LogWarningColor);
				Message msg = new Message(){
					Channel = logChannel,
					DesiredType = (server.Config.ModChannelEmbeds) ? MessageType.Embed : MessageType.String,
					LogEmbed = GetLogEmbed(color, "", "User warned",
						"by: " + (issuedBy?.GetUsername() ?? "<unknown>"),
						userNames.ToNamesList() ?? "<unknown>", userIds.Select(id => id.ToString()).ToNamesList(),
						DateTime.UtcNow,
						"Warning", warning),
					LogString = GetLogMessage("User warned ", (issuedBy == null ? "by unknown" : "by " + issuedBy.GetUsername()),
						userNames.ToNamesList() ?? "", userIds.Select(id => id.ToString()).ToNamesList(),
						Utils.GetTimestamp(),
						"Warning", warning)
				};
				await this.MessageQueueLock.WaitAsync();
				this.MessageQueue.Add(msg);
				this.MessageQueueLock.Release();
			}
			catch( HttpException exception )
			{
				await server.HandleHttpException(exception);
			}
			catch( Exception exception )
			{
				await this.HandleException(exception, "LogBan", server.Id);
			}
		}

		private async Task LogBan(Server server, string userName, guid userId, string reason, string duration, SocketGuildUser issuedBy)
		{
			try
			{
				SocketTextChannel logChannel;
				if( !server.Config.LogBans || (logChannel = server.Guild.GetTextChannel(server.Config.ModChannelId)) == null )
					return;

				this.RecentlyBannedUserIDs.Add(userId); //Don't trigger the on-event log message as well as this custom one.

				Color color = issuedBy?.Id == this.Client.GlobalConfig.UserId ? this.AntispamColor : new Color(server.Config.ModChannelColor);
				Message msg = new Message(){
					Channel = logChannel,
					DesiredType = (server.Config.ModChannelEmbeds) ? MessageType.Embed : MessageType.String,
					LogEmbed = GetLogEmbed(color, "", "User Banned " + duration,
						"by: " + (issuedBy?.GetUsername() ?? "<unknown>"),
						userName ?? "<unknown>", $"`{userId.ToString()}`",
						DateTime.UtcNow,
						"Reason", reason),
					LogString = GetLogMessage("User Banned " + duration, (issuedBy == null ? "by unknown" : "by " + issuedBy.GetUsername()),
						userName ?? "", userId.ToString(),
						Utils.GetTimestamp(),
						"Reason", reason)
				};
				await this.MessageQueueLock.WaitAsync();
				this.MessageQueue.Add(msg);
				this.MessageQueueLock.Release();
			}
			catch( HttpException exception )
			{
				await server.HandleHttpException(exception);
			}
			catch(Exception exception)
			{
				await this.HandleException(exception, "LogBan", server.Id);
			}
		}

		private async Task LogUnban(Server server, string userName, guid userId, SocketGuildUser issuedBy)
		{
			try
			{
				SocketTextChannel logChannel;
				if( !server.Config.LogBans || (logChannel = server.Guild.GetTextChannel(server.Config.ModChannelId)) == null )
					return;

				this.RecentlyUnbannedUserIDs.Add(userId); //Don't trigger the on-event log message as well as this custom one.

				if( string.IsNullOrWhiteSpace(userName) )
					userName = "<unknown>";

				Message msg = new Message(){
					Channel = logChannel,
					DesiredType = (server.Config.ModChannelEmbeds) ? MessageType.Embed : MessageType.String,
					LogEmbed = GetLogEmbed(new Color(server.Config.ModChannelColor), "", "User Unbanned",
						"by: " + (issuedBy?.GetUsername() ?? "<unknown>"),
						userName, $"`{userId.ToString()}`",
						DateTime.UtcNow),
					LogString = GetLogMessage("User Unbanned", (issuedBy == null ? "by unknown" : "by " + issuedBy.GetUsername()),
						userName, userId.ToString(),
						Utils.GetTimestamp())
				};
				await this.MessageQueueLock.WaitAsync();
				this.MessageQueue.Add(msg);
				this.MessageQueueLock.Release();
			}
			catch( HttpException exception )
			{
				await server.HandleHttpException(exception);
			}
			catch(Exception exception)
			{
				await this.HandleException(exception, "LogUnban", server.Id);
			}
		}

		private async Task LogKick(Server server, string userName, guid userId, string reason, SocketGuildUser issuedBy)
		{
			try
			{
				SocketTextChannel logChannel;
				if( !server.Config.LogBans || (logChannel = server.Guild.GetTextChannel(server.Config.ModChannelId)) == null )
					return;

				this.RecentlyBannedUserIDs.Add(userId); //Don't trigger the on-event log message as well as this custom one.

				Color color = issuedBy.Id == this.Client.GlobalConfig.UserId ? this.AntispamColor : new Color(server.Config.ModChannelColor);
				Message msg = new Message(){
					Channel = logChannel,
					DesiredType = (server.Config.ModChannelEmbeds) ? MessageType.Embed : MessageType.String,
					LogEmbed = GetLogEmbed(color, "", "User Kicked",
						"by: " + (issuedBy?.GetUsername() ?? "<unknown>"),
						userName ?? "<unknown>", $"`{userId.ToString()}`",
						DateTime.UtcNow,
						"Reason", reason),
					LogString = GetLogMessage("User Kicked", (issuedBy == null ? "by unknown" : "by " + issuedBy.GetUsername()),
						userName ?? "", userId.ToString(),
						Utils.GetTimestamp(),
						"Reason", reason)
				};
				await this.MessageQueueLock.WaitAsync();
				this.MessageQueue.Add(msg);
				this.MessageQueueLock.Release();
			}
			catch( HttpException exception )
			{
				await server.HandleHttpException(exception);
			}
			catch(Exception exception)
			{
				await this.HandleException(exception, "LogKick", server.Id);
			}
		}

		private async Task LogMute(Server server, SocketGuildUser user, string duration, SocketGuildUser issuedBy)
		{
			try
			{
				SocketTextChannel logChannel;
				if( !server.Config.LogBans || (logChannel = server.Guild.GetTextChannel(server.Config.ModChannelId)) == null )
					return;

				Color color = issuedBy.Id == this.Client.GlobalConfig.UserId ? this.AntispamColor : new Color(server.Config.ModChannelColor);
				Message msg = new Message(){
					Channel = logChannel,
					DesiredType = (server.Config.ModChannelEmbeds) ? MessageType.Embed : MessageType.String,
					LogEmbed = GetLogEmbed(color, user?.GetAvatarUrl(), "User muted " + duration,
						"by: " + (issuedBy?.GetUsername() ?? "<unknown>"),
						user.GetUsername(), $"`{user.Id.ToString()}`",
						DateTime.UtcNow),
					LogString = GetLogMessage("User Muted " + duration, (issuedBy == null ? "by unknown" : "by " + issuedBy.GetUsername()),
						user.GetUsername(), user.Id.ToString(),
						Utils.GetTimestamp())
				};
				await this.MessageQueueLock.WaitAsync();
				this.MessageQueue.Add(msg);
				this.MessageQueueLock.Release();
			}
			catch( HttpException exception )
			{
				await server.HandleHttpException(exception);
			}
			catch(Exception exception)
			{
				await this.HandleException(exception, "LogMute", server.Id);
			}
		}

		private async Task LogUnmute(Server server, SocketGuildUser user, SocketGuildUser issuedBy)
		{
			try
			{
				SocketTextChannel logChannel;
				if( !server.Config.LogBans || (logChannel = server.Guild.GetTextChannel(server.Config.ModChannelId)) == null )
					return;

				Message msg = new Message(){
					Channel = logChannel,
					DesiredType = (server.Config.ModChannelEmbeds) ? MessageType.Embed : MessageType.String,
					LogEmbed = GetLogEmbed(new Color(server.Config.ModChannelColor), user?.GetAvatarUrl(), "User Unmuted",
						"by: " + (issuedBy?.GetUsername() ?? "<unknown>"),
						user.GetUsername(), $"`{user.Id.ToString()}`",
						DateTime.UtcNow),
					LogString = GetLogMessage("User Unmuted ", (issuedBy == null ? "by unknown" : "by " + issuedBy.GetUsername()),
						user.GetUsername(), user.Id.ToString(),
						Utils.GetTimestamp())
				};
				await this.MessageQueueLock.WaitAsync();
				this.MessageQueue.Add(msg);
				this.MessageQueueLock.Release();
			}
			catch( HttpException exception )
			{
				await server.HandleHttpException(exception);
			}
			catch(Exception exception)
			{
				await this.HandleException(exception, "LogUnmute", server.Id);
			}
		}
		private async Task LogMutedChannel(Server server, SocketGuildChannel channel, string duration, SocketGuildUser issuedBy)
		{
			try
			{
				SocketTextChannel logChannel;
				if( !server.Config.LogBans || (logChannel = server.Guild.GetTextChannel(server.Config.ModChannelId)) == null )
					return;

				Color color = issuedBy.Id == this.Client.GlobalConfig.UserId ? this.AntispamColor : new Color(server.Config.ModChannelColor);
				Message msg = new Message(){
					Channel = logChannel,
					DesiredType = (server.Config.ModChannelEmbeds) ? MessageType.Embed : MessageType.String,
					LogEmbed = GetLogEmbed(color, "", "Channel muted " + duration,
						"by: " + (issuedBy?.GetUsername() ?? "<unknown>"),
						"#" + channel.Name, $"`{channel.Id.ToString()}`",
						DateTime.UtcNow),
					LogString = GetLogMessage("Channel Muted " + duration, (issuedBy == null ? "by unknown" : "by " + issuedBy.GetUsername()),
						"#" + channel.Name, channel.Id.ToString(),
						Utils.GetTimestamp())
				};
				await this.MessageQueueLock.WaitAsync();
				this.MessageQueue.Add(msg);
				this.MessageQueueLock.Release();
			}
			catch( HttpException exception )
			{
				await server.HandleHttpException(exception);
			}
			catch(Exception exception)
			{
				await this.HandleException(exception, "LogMute", server.Id);
			}
		}

		private async Task LogUnmutedChannel(Server server, SocketGuildChannel channel, SocketUser issuedBy)
		{
			try
			{
				SocketTextChannel logChannel;
				if( !server.Config.LogBans || (logChannel = server.Guild.GetTextChannel(server.Config.ModChannelId)) == null )
					return;

				Message msg = new Message(){
					Channel = logChannel,
					DesiredType = (server.Config.ModChannelEmbeds) ? MessageType.Embed : MessageType.String,
					LogEmbed = GetLogEmbed(new Color(server.Config.ModChannelColor), "", "Channel Unmuted",
						"by: " + (issuedBy?.GetUsername() ?? "<unknown>"),
						"#" + channel.Name, $"`{channel.Id.ToString()}`",
						DateTime.UtcNow),
					LogString = GetLogMessage("Channel Unmuted ", (issuedBy == null ? "by unknown" : "by " + issuedBy.GetUsername()),
						"#" + channel.Name, channel.Id.ToString(),
						Utils.GetTimestamp())
				};
				await this.MessageQueueLock.WaitAsync();
				this.MessageQueue.Add(msg);
				this.MessageQueueLock.Release();
			}
			catch( HttpException exception )
			{
				await server.HandleHttpException(exception);
			}
			catch(Exception exception)
			{
				await this.HandleException(exception, "LogUnmute", server.Id);
			}
		}


		private async Task LogPublicRoleJoin(Server server, SocketGuildUser user, string roleName)
		{
			try
			{
				SocketTextChannel logChannel;
				if( !server.Config.LogPromotions || (logChannel = server.Guild.GetTextChannel(server.Config.LogChannelId)) == null )
					return;

				Message msg = new Message(){
					Channel = logChannel,
					DesiredType = (server.Config.LogChannelEmbeds) ? MessageType.Embed : MessageType.String,
					LogEmbed = GetLogSmolEmbed(new Color(server.Config.LogChannelColor),
						user.GetUsername() + " joined a publicRole:",
						user.GetAvatarUrl(), roleName,
						Utils.GetTimestamp(DateTime.UtcNow), DateTime.UtcNow),
					LogString = $"`{Utils.GetTimestamp()}`: **{user.GetUsername()}** joined the `{roleName}` public role."
				};
				await this.MessageQueueLock.WaitAsync();
				this.MessageQueue.Add(msg);
				this.MessageQueueLock.Release();
			}
			catch( HttpException exception )
			{
				await server.HandleHttpException(exception);
			}
			catch(Exception exception)
			{
				await this.HandleException(exception, "LogPublicRoleJoin", server.Id);
			}
		}

		private async Task LogPublicRoleLeave(Server server, SocketGuildUser user, string roleName)
		{
			try
			{
				SocketTextChannel logChannel;
				if( !server.Config.LogPromotions || (logChannel = server.Guild.GetTextChannel(server.Config.LogChannelId)) == null )
					return;

				Message msg = new Message(){
					Channel = logChannel,
					DesiredType = (server.Config.LogChannelEmbeds) ? MessageType.Embed : MessageType.String,
					LogEmbed = GetLogSmolEmbed(new Color(server.Config.LogChannelColor),
						user.GetUsername() + " left a publicRole:",
						user.GetAvatarUrl(), roleName,
						Utils.GetTimestamp(DateTime.UtcNow), DateTime.UtcNow),
					LogString = $"`{Utils.GetTimestamp()}`: **{user.GetUsername()}** left the `{roleName}` public role."
				};
				await this.MessageQueueLock.WaitAsync();
				this.MessageQueue.Add(msg);
				this.MessageQueueLock.Release();
			}
			catch( HttpException exception )
			{
				await server.HandleHttpException(exception);
			}
			catch(Exception exception)
			{
				await this.HandleException(exception, "LogPublicRoleLeave", server.Id);
			}
		}

		private async Task LogPromote(Server server, SocketGuildUser user, string roleName, SocketGuildUser issuedBy)
		{
			try
			{
				SocketTextChannel logChannel;
				if( !server.Config.LogPromotions || (logChannel = server.Guild.GetTextChannel(server.Config.LogChannelId)) == null )
					return;

				Message msg = new Message(){
					Channel = logChannel,
					DesiredType = (server.Config.LogChannelEmbeds) ? MessageType.Embed : MessageType.String,
					LogEmbed = GetLogSmolEmbed(new Color(server.Config.LogChannelColor),
						user.GetUsername() + $" was promoted to the {roleName} memberRole.",
						user.GetAvatarUrl(),
						"Promoted by: " + (issuedBy?.GetUsername() ?? "<unknown>"),
						Utils.GetTimestamp(DateTime.UtcNow), DateTime.UtcNow),
					LogString = $"`{Utils.GetTimestamp()}`: **{user.GetUsername()}** was promoted to the `{roleName}` member role by __{issuedBy.GetUsername()}__"
				};
				await this.MessageQueueLock.WaitAsync();
				this.MessageQueue.Add(msg);
				this.MessageQueueLock.Release();
			}
			catch( HttpException exception )
			{
				await server.HandleHttpException(exception);
			}
			catch(Exception exception)
			{
				await this.HandleException(exception, "LogPromote", server.Id);
			}
		}

		private async Task LogDemote(Server server, SocketGuildUser user, string roleName, SocketGuildUser issuedBy)
		{
			try
			{
				SocketTextChannel logChannel;
				if( !server.Config.LogPromotions || (logChannel = server.Guild.GetTextChannel(server.Config.LogChannelId)) == null )
					return;

				Message msg = new Message(){
					Channel = logChannel,
					DesiredType = (server.Config.LogChannelEmbeds) ? MessageType.Embed : MessageType.String,
					LogEmbed = GetLogSmolEmbed(new Color(server.Config.LogChannelColor),
						user.GetUsername() + $" was demoted from the {roleName} memberRole.",
						user.GetAvatarUrl(),
						"Demoted by: " + (issuedBy?.GetUsername() ?? "<unknown>"),
						Utils.GetTimestamp(DateTime.UtcNow), DateTime.UtcNow),
					LogString = $"`{Utils.GetTimestamp()}`: **{user.GetUsername()}** was demoted from the `{roleName}` member role by __{issuedBy.GetUsername()}__"
				};
				await this.MessageQueueLock.WaitAsync();
				this.MessageQueue.Add(msg);
				this.MessageQueueLock.Release();
			}
			catch( HttpException exception )
			{
				await server.HandleHttpException(exception);
			}
			catch(Exception exception)
			{
				await this.HandleException(exception, "LogDemote", server.Id);
			}
		}


		private static string GetLogMessage(string titleRed, string infoGreen, string nameGold, string idGreen,
			guid timestampId, string tag1 = "", string msg1 = "", string tag2 = "", string msg2 = "")
			=> GetLogMessage(titleRed, infoGreen, nameGold, idGreen,
				Utils.GetTimestamp(Utils.GetTimeFromId(timestampId)),
				tag1, msg1, tag2, msg2);

		private static string GetLogMessage(string titleRed, string infoGreen, string nameGold, string idGreen,
			string timestamp, string tag1 = "", string msg1 = "", string tag2 = "", string msg2 = "")
		{
			msg1 = msg1.Replace('`', '\'');
			msg2 = msg2.Replace('`', '\'');
			int length = titleRed.Length + infoGreen.Length + nameGold.Length + idGreen.Length + msg1.Length + msg2.Length + timestamp.Length + 100;
			int messageLimit = 1500;
			while( length >= GlobalConfig.MessageCharacterLimit )
			{
				msg1 = msg1.Substring(0, Math.Min(messageLimit, msg1.Length)) + "**...**";
				if( !string.IsNullOrWhiteSpace(msg2) )
					msg2 = msg2.Substring(0, Math.Min(messageLimit, msg2.Length)) + "**...**";

				length = titleRed.Length + infoGreen.Length + nameGold.Length + idGreen.Length + msg1.Length + msg2.Length + timestamp.Length + 100;
				messageLimit -= 100;
			}

			string message = "";
			string tag = "";
			if( string.IsNullOrWhiteSpace(tag1) && !string.IsNullOrWhiteSpace(msg1) )
				message += msg1;
			else if( !string.IsNullOrWhiteSpace(tag1) && !string.IsNullOrWhiteSpace(msg1) )
			{
				tag = "<" + tag1;
				while( tag.Length < 9 )
					tag += " ";
				message += tag + "> " + msg1;
			}

			if( string.IsNullOrWhiteSpace(tag2) && !string.IsNullOrWhiteSpace(msg2) )
				message += "\n" + msg2;
			else if( !string.IsNullOrWhiteSpace(tag2) && !string.IsNullOrWhiteSpace(msg2) )
			{
				tag = "<" + tag2;
				while( tag.Length < 9 )
					tag += " ";
				message += "\n" + tag + "> " + msg2;
			}

			return string.Format("```md\n# {0}\n[{1}]({2})\n< {3} ={4}>\n{5}\n```", titleRed, timestamp, infoGreen, nameGold, idGreen, message);
		}


		private static Embed GetLogEmbed(Color color, string iconUrl, string title, string description, string name, string id,
			guid timestampId, string tag1 = "", string msg1 = "", string tag2 = "", string msg2 = "")
			=> GetLogEmbed(color, iconUrl, title, description, name, id,
				Utils.GetTimeFromId(timestampId),
				tag1, msg1, tag2, msg2);

		private static Embed GetLogEmbed(Color color, string iconUrl,string title, string description, string name, string id,
			DateTime timestamp, string tag1 = "", string msg1 = "", string tag2 = "", string msg2 = "")
		{
			if( msg1.Length > 1000 )
				msg1 = msg1.Substring(0, 1000) + "**...**";
			if( msg2.Length > 1000 )
				msg2 = msg2.Substring(0, 1000) + "**...**";

			msg1 = msg1.Replace('`', '\'');
			msg2 = msg2.Replace('`', '\'');

			EmbedBuilder embedBuilder = new EmbedBuilder{
					Color = color,
					Description = description,
					Timestamp = timestamp
				}.WithAuthor(title, iconUrl)
				 .WithFooter(Utils.GetTimestamp(timestamp));

			embedBuilder.AddField("Name", name, true);
			embedBuilder.AddField("Id", id, true);

			if( !string.IsNullOrEmpty(tag1) && !string.IsNullOrEmpty(msg1) )
				embedBuilder.AddField(tag1, msg1, false);
			if( !string.IsNullOrEmpty(tag2) && !string.IsNullOrEmpty(msg2) )
				embedBuilder.AddField(tag2, msg2, false);

			return embedBuilder.Build();
		}

		private static Embed GetLogSmolEmbed(Color color, string title, string titleIconUrl, string description, string footer, DateTime timestamp)
		{
			EmbedBuilder embedBuilder = new EmbedBuilder{
					Color = color,
					Description = description,
					Timestamp = timestamp
				}.WithAuthor(title, titleIconUrl)
				 .WithFooter(footer);

			return embedBuilder.Build();
		}


		public Task Update(IBotwinderClient iClient)
		{
			return Task.CompletedTask;
			/*if( this.LastUpdateTime + this.UpdateDelay > DateTime.UtcNow )
				return Task.CompletedTask;

			this.LastUpdateTime = DateTime.UtcNow;

			this.RecentlyBannedUserIDs.Clear();
			this.RecentlyUnbannedUserIDs.Clear();

			return Task.CompletedTask;*/
		}
	}
}
