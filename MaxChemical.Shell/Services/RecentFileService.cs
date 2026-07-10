using Google.Protobuf.Collections;
using MaxChemical.Logging;
using MaxChemical.Shell.Events;
using MaxChemical.Shell.Models;
using Newtonsoft.Json;
using Prism.Events;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Shell.Services
{
    public sealed class RecentFileService : IRecentFileService
    {
        private readonly string _path;
        private readonly List<RecentFileItem> _collection;

        private readonly IEventAggregator _aggregator;
        private readonly ILogService _logger;


        public RecentFileService(IEventAggregator aggregator, ILogService logger)
        {
            _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _path = Path.Combine(Directory.GetCurrentDirectory(), "RecentFiles.json");
            _collection = new List<RecentFileItem>();
        }


        public async Task LoadAsync()
        {
            try
            {
                // 判断配置文件是否存在
                if (!File.Exists(_path))
                {
                    return;
                }

                // 获取json字符串
                string json = await File.ReadAllTextAsync(_path);

                // 反序列化 json
                var lists = JsonConvert.DeserializeObject<List<RecentFileItem>>(json);

                // 按最后访问时间倒序
                lists = lists?.OrderByDescending(items => items.LastAccessed).ToList() ?? new List<RecentFileItem>();
                foreach (var item in lists)
                {
                    _collection.Add(item);
                }

                // 发布集合改变事件
                _aggregator.GetEvent<RecentFilesChangedEvent>()?.Publish(_collection.ToList());
            }
            catch (Exception e)
            {
                _logger.LogError(e, "加载最近打开文件配置文件失败");
            }
        }

        public void Add(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (!File.Exists(path))
                {
                    return;
                }
                // 先移除匹配项
                _collection.RemoveAll(item => item.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
                // 插入到集合头部
                _collection.Insert(0, new RecentFileItem()
                {
                    FileName = Path.GetFileName(path),
                    FilePath = path,
                    LastAccessed = DateTime.Now
                });

                // 保留最近10项
                while (_collection.Count > 10)
                {
                    _collection.RemoveAt(_collection.Count - 1);
                }

                // 发布集合改变事件
                _aggregator.GetEvent<RecentFilesChangedEvent>()?.Publish(_collection.ToList());
            }
            catch (Exception e)
            {
                _logger.LogError(e, "加载最近打开文件配置文件失败");
            }

            Save();
        }

        public void Remove(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                // 移除匹配项
                int count = _collection.RemoveAll(item => item.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (count < 1)
                {
                    return;
                }

                // 发布集合改变事件
                _aggregator.GetEvent<RecentFilesChangedEvent>()?.Publish(_collection.ToList());
            }
            catch (Exception e)
            {
                _logger.LogError(e, "移除指定的文件信息失败");
            }

            Save();
        }

        public void Clear()
        {
            try
            {
                _collection.Clear();
                if (!File.Exists(_path))
                {
                    File.Delete(_path);
                }
                // 发布集合改变事件
                _aggregator.GetEvent<RecentFilesChangedEvent>()?.Publish(_collection.ToList());
            }
            catch (Exception e)
            {
                _logger.LogError(e, "清除所有记录失败");
            }
        }

        /// <summary>
        /// 私有方法，保存到配置文件
        /// </summary>
        private void Save()
        {
            // 集合是否无内容
            if (_collection.Any())
            {
                try
                {
                    string json = JsonConvert.SerializeObject(_collection, new JsonSerializerSettings() { Formatting = Formatting.Indented });
                    // 全部写入配置文件
                    File.WriteAllText(_path, json);
                }
                catch(Exception e)
                {
                    _logger.LogError(e, "保存最近打开文件配置文件失败");
                }
            }
        }
    }



    public interface IRecentFileService
    {
        /// <summary>
        /// 加载最近打开文件配置文件
        /// </summary>
        Task LoadAsync();

        /// <summary>
        /// 添加文件
        /// </summary>
        /// <param name="path"></param>
        void Add(string path);

        /// <summary>
        /// 移除文件
        /// </summary>
        /// <param name="path"></param>
        void Remove(string path);

        //void Save();

        /// <summary>
        /// 清除所有内容
        /// </summary>
        void Clear();
    }
}
