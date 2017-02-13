﻿using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using NadekoBot.Attributes;
using NadekoBot.Extensions;
using NadekoBot.Services;
using NadekoBot.Services.Database.Models;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace NadekoBot.Modules.Games
{
    public partial class Games
    {
        /// <summary>
        /// Flower picking/planting idea is given to me by its
        /// inceptor Violent Crumble from Game Developers League discord server
        /// (he has !cookie and !nom) Thanks a lot Violent!
        /// Check out GDL (its a growing gamedev community):
        /// https://discord.gg/0TYNJfCU4De7YIk8
        /// </summary>
        [Group]
        public class PlantPickCommands : ModuleBase
        {
            private static ConcurrentHashSet<ulong> generationChannels { get; } = new ConcurrentHashSet<ulong>();
            //channelid/message
            private static ConcurrentDictionary<ulong, List<IUserMessage>> plantedFlowers { get; } = new ConcurrentDictionary<ulong, List<IUserMessage>>();
            //channelId/last generation
            private static ConcurrentDictionary<ulong, DateTime> lastGenerations { get; } = new ConcurrentDictionary<ulong, DateTime>();

            private static ConcurrentHashSet<ulong> usersRecentlyPicked { get; } = new ConcurrentHashSet<ulong>();

            private static Logger _log { get; }

            static PlantPickCommands()
            {
                _log = LogManager.GetCurrentClassLogger();

#if !GLOBAL_NADEKO
                NadekoBot.Client.MessageReceived += PotentialFlowerGeneration;
#endif
                generationChannels = new ConcurrentHashSet<ulong>(NadekoBot.AllGuildConfigs
                    .SelectMany(c => c.GenerateCurrencyChannelIds.Select(obj => obj.ChannelId)));
            }

            private static Task PotentialFlowerGeneration(SocketMessage imsg)
            {
                var msg = imsg as SocketUserMessage;
                if (msg == null || msg.IsAuthor() || msg.Author.IsBot)
                    return Task.CompletedTask;

                var channel = imsg.Channel as ITextChannel;
                if (channel == null)
                    return Task.CompletedTask;

                if (!generationChannels.Contains(channel.Id))
                    return Task.CompletedTask;

                var _ = Task.Run(async () =>
                {
                    try
                    {
                        var lastGeneration = lastGenerations.GetOrAdd(channel.Id, DateTime.MinValue);
                        var rng = new NadekoRandom();

                        //todo i'm stupid :rofl: wtg kwoth. real async programming :100: :ok_hand: :100: :100: :thumbsup:
                        if (DateTime.Now - TimeSpan.FromSeconds(NadekoBot.BotConfig.CurrencyGenerationCooldown) < lastGeneration) //recently generated in this channel, don't generate again
                            return;

                        var num = rng.Next(1, 101) + NadekoBot.BotConfig.CurrencyGenerationChance * 100;

                        if (num > 100)
                        {
                            lastGenerations.AddOrUpdate(channel.Id, DateTime.Now, (id, old) => DateTime.Now);

                            var dropAmount = NadekoBot.BotConfig.CurrencyDropAmount;

                            if (dropAmount > 0)
                            {
                                var msgs = new IUserMessage[dropAmount];

                                string firstPart;
                                if (dropAmount == 1)
                                {
                                    firstPart = $"A random { NadekoBot.BotConfig.CurrencyName } appeared!";
                                }
                                else
                                {
                                    firstPart = $"{dropAmount} random { NadekoBot.BotConfig.CurrencyPluralName } appeared!";
                                }
                                var file = GetRandomCurrencyImage();
                                using (var fileStream = file.Value.ToStream())
                                {
                                    var sent = await channel.SendFileAsync(
                                        fileStream,
                                        file.Key,
                                        $"❗ {firstPart} Pick it up by typing `{NadekoBot.ModulePrefixes[typeof(Games).Name]}pick`")
                                            .ConfigureAwait(false);

                                    msgs[0] = sent;
                                }

                                plantedFlowers.AddOrUpdate(channel.Id, msgs.ToList(), (id, old) => { old.AddRange(msgs); return old; });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warn(ex);
                    }
                });
                return Task.CompletedTask;
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            public async Task Pick()
            {
                var channel = (ITextChannel)Context.Channel;

                if (!(await channel.Guild.GetCurrentUserAsync()).GetPermissions(channel).ManageMessages)
                    return;
#if GLOBAL_NADEKO
                if (!usersRecentlyPicked.Add(Context.User.Id))
                    return;
#endif
                try
                {

                    List<IUserMessage> msgs;

                    try { await Context.Message.DeleteAsync().ConfigureAwait(false); } catch { }
                    if (!plantedFlowers.TryRemove(channel.Id, out msgs))
                        return;

                    await Task.WhenAll(msgs.Where(m => m != null).Select(toDelete => toDelete.DeleteAsync())).ConfigureAwait(false);

                    await CurrencyHandler.AddCurrencyAsync((IGuildUser)Context.User, $"Picked {NadekoBot.BotConfig.CurrencyPluralName}", msgs.Count, false).ConfigureAwait(false);
                    var msg = await channel.SendConfirmAsync($"**{Context.User}** picked {msgs.Count}{NadekoBot.BotConfig.CurrencySign}!").ConfigureAwait(false);
                    msg.DeleteAfter(10);
                }
                finally
                {
#if GLOBAL_NADEKO
                    await Task.Delay(60000);
                    usersRecentlyPicked.TryRemove(Context.User.Id);
#endif
                }
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            public async Task Plant(int amount = 1)
            {
                if (amount < 1)
                    return;

                var removed = await CurrencyHandler.RemoveCurrencyAsync((IGuildUser)Context.User, $"Planted a {NadekoBot.BotConfig.CurrencyName}", amount, false).ConfigureAwait(false);
                if (!removed)
                {
                    await Context.Channel.SendErrorAsync($"You don't have enough {NadekoBot.BotConfig.CurrencyPluralName}.").ConfigureAwait(false);
                    return;
                }

                var imgData = GetRandomCurrencyImage();
                var vowelFirst = new[] { 'a', 'e', 'i', 'o', 'u' }.Contains(NadekoBot.BotConfig.CurrencyName[0]);

                var msgToSend = $"Oh how Nice! **{Context.User.Username}** planted {(amount == 1 ? (vowelFirst ? "an" : "a") : amount.ToString())} {(amount > 1 ? NadekoBot.BotConfig.CurrencyPluralName : NadekoBot.BotConfig.CurrencyName)}. Pick it using {NadekoBot.ModulePrefixes[typeof(Games).Name]}pick";

                IUserMessage msg;
                using (var toSend = imgData.Value.ToStream())
                {
                    msg = await Context.Channel.SendFileAsync(toSend, imgData.Key, msgToSend).ConfigureAwait(false);
                }

                var msgs = new IUserMessage[amount];
                msgs[0] = msg;

                plantedFlowers.AddOrUpdate(Context.Channel.Id, msgs.ToList(), (id, old) =>
                {
                    old.AddRange(msgs);
                    return old;
                });
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [RequireUserPermission(GuildPermission.ManageMessages)]
            public async Task GenCurrency()
            {
                var channel = (ITextChannel)Context.Channel;

                bool enabled;
                using (var uow = DbHandler.UnitOfWork())
                {
                    var guildConfig = uow.GuildConfigs.For(channel.Id, set => set.Include(gc => gc.GenerateCurrencyChannelIds));

                    var toAdd = new GCChannelId() { ChannelId = channel.Id };
                    if (!guildConfig.GenerateCurrencyChannelIds.Contains(toAdd))
                    {
                        guildConfig.GenerateCurrencyChannelIds.Add(toAdd);
                        generationChannels.Add(channel.Id);
                        enabled = true;
                    }
                    else
                    {
                        guildConfig.GenerateCurrencyChannelIds.Remove(toAdd);
                        generationChannels.TryRemove(channel.Id);
                        enabled = false;
                    }
                    await uow.CompleteAsync();
                }
                if (enabled)
                {
                    await channel.SendConfirmAsync("Currency generation enabled on this channel.").ConfigureAwait(false);
                }
                else
                {
                    await channel.SendConfirmAsync("Currency generation disabled on this channel.").ConfigureAwait(false);
                }
            }

            private static KeyValuePair<string, ImmutableArray<byte>> GetRandomCurrencyImage()
            {
                var rng = new NadekoRandom();
                var images = NadekoBot.Images.Currency;

                return images[rng.Next(0, images.Length)];
            }

            int GetRandomNumber()
            {
                using (var rg = RandomNumberGenerator.Create())
                {
                    byte[] rno = new byte[4];
                    rg.GetBytes(rno);
                    int randomvalue = BitConverter.ToInt32(rno, 0);
                    return randomvalue;
                }
            }
        }
    }
}