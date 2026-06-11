using MaxChemical.Shell.Models;
using Prism.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Shell.Events
{
    /// <summary>
    /// 最近打开文件信息集合改变事件
    /// </summary>
    public sealed class RecentFilesChangedEvent : PubSubEvent<List<RecentFileItem>> { }
}
