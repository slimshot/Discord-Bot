﻿using CodeHollow.FeedReader.Feeds;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using NadekoBot.Core.Services;
using NadekoBot.Core.Services.Database.Models;
using NadekoBot.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;

namespace NadekoBot.Modules.Searches.Services
{
    public class FeedsService : INService
    {
        private readonly DbService _db;
        private readonly ConcurrentDictionary<string, HashSet<FeedSub>> _subs;
        private readonly DiscordSocketClient _client;
        private readonly ConcurrentDictionary<string, DateTime> _lastPosts =
            new ConcurrentDictionary<string, DateTime>();

        private Logger _log;

        public FeedsService(NadekoBot bot, DbService db, DiscordSocketClient client)
        {
            _log = LogManager.GetCurrentClassLogger();
            _log.Info($"Loading {this.GetType().Name}.");
            _db = db;

            _subs = bot
                .AllGuildConfigs
                .SelectMany(x => x.FeedSubs)
                .GroupBy(x => x.Url.ToLower())
                .ToDictionary(x => x.Key, x => x.ToHashSet())
                .ToConcurrent();

            _client = client;

            var _ = Task.Run(TrackFeeds);
            _log.Info($"Loaded {this.GetType().Name}.");
        }

        public async Task<EmbedBuilder> TrackFeeds()
        {
            while (true)
            {
                var allSendTasks = new List<Task>(_subs.Count);
                foreach (var kvp in _subs)
                {
                    if (kvp.Value.Count == 0)
                        continue;

                    var rssUrl = kvp.Key;
                    try
                    {
                        var feed = await CodeHollow.FeedReader.FeedReader.ReadAsync(rssUrl).ConfigureAwait(false);

                        var embed = new EmbedBuilder()
                            .WithFooter(rssUrl);

                        var items = feed
                            .Items
                            .Select(item => (Item: item, LastUpdate: item.PublishingDate?.ToUniversalTime()
                                                                  ?? (item.SpecificItem as AtomFeedItem)?.UpdatedDate?.ToUniversalTime()))
                            .Where(data => !(data.LastUpdate is null))
                            .Select(data => (data.Item, LastUpdate: (DateTime)data.LastUpdate))
                            .OrderByDescending(data => data.LastUpdate)
                            .Reverse() // start from the oldest
                            .ToList();

                        if (!_lastPosts.TryGetValue(kvp.Key, out DateTime lastFeedUpdate))
                        {
                            lastFeedUpdate = _lastPosts[kvp.Key] = items.Any() ? items[items.Count - 1].LastUpdate : DateTime.UtcNow;
                        }

                        foreach (var (feedItem, itemUpdateDate) in items)
                        {
                            if (itemUpdateDate <= lastFeedUpdate)
                            {
                                continue;
                            }

                            _lastPosts[kvp.Key] = itemUpdateDate;

                            var link = feedItem.SpecificItem.Link;
                            if (!string.IsNullOrWhiteSpace(link) && Uri.IsWellFormedUriString(link, UriKind.Absolute))
                                embed.WithUrl(link);

                            var title = string.IsNullOrWhiteSpace(feedItem.Title)
                                ? "-"
                                : feedItem.Title;

                            if (feedItem.SpecificItem is MediaRssFeedItem mrfi && (mrfi.Enclosure?.MediaType.StartsWith("image/") ?? false))
                            {
                                var imgUrl = mrfi.Enclosure.Url;
                                if (!string.IsNullOrWhiteSpace(imgUrl) && Uri.IsWellFormedUriString(imgUrl, UriKind.Absolute))
                                {
                                    embed.WithImageUrl(imgUrl);
                                }
                            }

                            //// old image retreiving code
                            //var img = (item as Rss20Feed).Items.FirstOrDefault(x => x.Element.Name == "enclosure") ...FirstOrDefault(x => x.RelationshipType == "enclosure")?.Uri.AbsoluteUri
                            //    ?? Regex.Match(item.Description, @"src=""(?<src>.*?)""").Groups["src"].ToString();

                            embed.WithTitle(title.TrimTo(256));

                            var desc = feedItem.Description?.StripHTML();
                            if (!string.IsNullOrWhiteSpace(feedItem.Description))
                                embed.WithDescription(desc.TrimTo(2048));

                            //send the created embed to all subscribed channels
                            var feedSendTasks = kvp.Value
                                .Where(x => x.GuildConfig != null)
                                .Select(x => _client.GetGuild(x.GuildConfig.GuildId)
                                    ?.GetTextChannel(x.ChannelId))
                                .Where(x => x != null)
                                .Select(x => x.EmbedAsync(embed));

                            allSendTasks.Add(Task.WhenAll(feedSendTasks));
                        }
                    }
                    catch { }
                }

                await Task.WhenAll(Task.WhenAll(allSendTasks), Task.Delay(10000)).ConfigureAwait(false);
            }
        }

        public List<FeedSub> GetFeeds(ulong guildId)
        {
            using (var uow = _db.GetDbContext())
            {
                return uow.GuildConfigs.ForId(guildId, set => set.Include(x => x.FeedSubs))
                    .FeedSubs
                    .OrderBy(x => x.Id)
                    .ToList();
            }
        }

        public bool AddFeed(ulong guildId, ulong channelId, string rssFeed)
        {
            rssFeed.ThrowIfNull(nameof(rssFeed));

            var fs = new FeedSub()
            {
                ChannelId = channelId,
                Url = rssFeed.Trim(),
            };

            using (var uow = _db.GetDbContext())
            {
                var gc = uow.GuildConfigs.ForId(guildId, set => set.Include(x => x.FeedSubs));

                if (gc.FeedSubs.Any(x => x.Url.ToLower() == fs.Url.ToLower()))
                {
                    return false;
                }
                else if (gc.FeedSubs.Count >= 10)
                {
                    return false;
                }

                gc.FeedSubs.Add(fs);
                uow.SaveChanges();
                //adding all, in case bot wasn't on this guild when it started
                foreach (var feed in gc.FeedSubs)
                {
                    _subs.AddOrUpdate(feed.Url.ToLower(), new HashSet<FeedSub>() { feed }, (k, old) =>
                    {
                        old.Add(feed);
                        return old;
                    });
                }

            }

            return true;
        }

        public bool RemoveFeed(ulong guildId, int index)
        {
            if (index < 0)
                return false;

            using (var uow = _db.GetDbContext())
            {
                var items = uow.GuildConfigs.ForId(guildId, set => set.Include(x => x.FeedSubs))
                    .FeedSubs
                    .OrderBy(x => x.Id)
                    .ToList();

                if (items.Count <= index)
                    return false;
                var toRemove = items[index];
                _subs.AddOrUpdate(toRemove.Url.ToLower(), new HashSet<FeedSub>(), (key, old) =>
                {
                    old.Remove(toRemove);
                    return old;
                });
                uow._context.Remove(toRemove);
                uow.SaveChanges();
            }
            return true;
        }
    }
}
