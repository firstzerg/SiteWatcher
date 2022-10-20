using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Forms = System.Windows.Forms;

namespace SiteWatcher
{
    public partial class AppWindowModel : BaseWindowModel<AppWindow>
    {
        public SortableBindingList<Watch> Watches {get;set;}=new SortableBindingList<Watch>(new List<Watch>());
        public SortableBindingList<WatchTag> Tags {get;set;}=new(new List<WatchTag>());
        private bool TagsUpdating = false;
        public bool ShowNew { 
            get=>showNew; 
            set{
                SetField(ref showNew, value);
                FilterWatches();
            }
        }
        private bool showNew;
        public string? TextFilter { 
            get=>textFilter; 
            set{
                if(TagsUpdating) return;
                SetField(ref textFilter, value);
                FilterWatches();
            }
        }
        private string? textFilter;
        private Stack<Watch> Trash {get;set;} = new();
        public bool Trashed {get{
            return Trash.Count>0;
        }}
        private ListView WatchList {get;set;}
        private Timer timer;
        private string oldConfig="";
        private string oldConfig2="";
        public Command CheckAllCommand{get;set;}
        public Command AddWatchCommand{get;set;}
        public Command CloseWindow{get;set;}
        public Command<Watch> DeleteWatchCommand{get;set;}
        public Command<Watch> CheckWatchCommand{get;set;}
        public Command<Watch> ToggleWatchCommand{get;set;}
        public Command<Watch> EditWatchCommand{get;set;}
        public Command<Watch> CopyWatchCommand{get;set;}
        public Command<Watch> NavigateWatchCommand{get;set;}
        public Command<Watch> ToggleReadWatchCommand{get;set;}
        public Command<Watch> CheckpointsCommand{get;set;}
        public Command UndeleteWatchCommand {get;set;}
        public Command ConfigCommand {get;set;}
        public Command ShowNewCommand {get;set;}
        public Command ClearFilterCommand {get;set;}
        
        public AppWindowModel(AppWindow win) : base(win){
            WatchList=win.WatchList;
            InitIcon();
            ConfigLoad();
            win.Closed+=(o,e)=>{ConfigSave();ConfigSave2();};
            CheckWatchCommand=new(w=>CheckSelectedWatch(w));
            AddWatchCommand=new(n=>AddWatch());
            EditWatchCommand =new(w=>EditWatch(w));
            DeleteWatchCommand = new(w=>DeleteSelectedWatch(w));
            CheckAllCommand = new(w=>CheckAll());
            CopyWatchCommand = new(w=>CopyWatch(w));
            ToggleWatchCommand = new(w=>ToggleSelectedWatch(w));
            NavigateWatchCommand = new(w=>NavigateWatch(w));
            ToggleReadWatchCommand = new(w=>ToggleReadSelectedWatch(w));
            CheckpointsCommand = new(w=>CheckpointsWatch(w));
            UndeleteWatchCommand = new(o=>UndeleteWatch());
            ShowNewCommand = new(o=>{ShowNew=!ShowNew;});
            ClearFilterCommand = new(o=>{TextFilter=null;});
            ConfigCommand = new(o=>ShowConfig());
            CloseWindow = new(n=>win.Close());
            timer = new Timer(1000*60);
            timer.Elapsed += TimerCheck;
            timer.AutoReset = true;
            timer.Enabled = true;
            TimerCheck(timer,null);
            Tags.ListChanged+=TagsChanged;
            win.Loaded += (s,e)=>RefreshList();
        }

        private void TagsChanged(object? sender, ListChangedEventArgs e){
            textFilter=null;
            ChangedField(nameof(Tags));
            RefreshList();
        }

        private void ShowConfig(){
            ConfigWindow win = new();
            Watches.Where(w=>w.Tags.Count>0).ToList().ForEach(w=>{
                w.Tags.Where(wt=>!Tags.Any(t=>t.Name==wt.Name)).ToList().ForEach(t=>Tags.Add(t));
            });
            ConfigWindowModel model = new( Tags.Select(t=>t.Clone()).ToList(), win);
            if(win.ShowDialog()??false){
                Tags.Clear();
                model.Tags.ToList().ForEach(t=>Tags.Add(t));
            }
        }

        private void UndeleteWatch(){
            if(Trashed){
                Watches.Add(Trash.Pop());
                ChangedField(nameof(Trashed));
            }
        }

        private void TimerCheck(object? sender, ElapsedEventArgs? e){
            foreach (var watch in Watches){
                if(watch.Enabled && watch.LastCheck+watch.Interval<=DateTime.Now){
                    CheckWatch(watch);                    
                }
            }
        }

        public void ToggleReadSelectedWatch(Watch? w){
            List<Watch> toMark = (w==null)? WatchList.SelectedItems.Cast<Watch>().ToList(): new(){w};
            toMark.ForEach(w=>{
                if(w.Status==WatchStatus.New) NavigateWatch(w,false);
                else w.LastSeen= w.Diff.Prev.Time;
            });
        }
        public void NavigateWatch(Watch? w,bool open=true){
            w?.Navigate(open);
        }

        public void DeleteSelectedWatch(Watch? w){
            List<Watch> toDelete = (w==null)?WatchList.SelectedItems.Cast<Watch>().ToList():new(){w};
            toDelete.ForEach(w=>DeleteWatch(w));
        }
        public void DeleteWatch(Watch? w){
            if(w !=null && Watches.Contains(w)){
                Trash.Push(w);
                ChangedField(nameof(Trashed));
                Watches.Remove(w);
            }
        }
        private void CheckAll(){
            foreach (var watch in Watches){
                if(watch.Enabled)
                    CheckWatch(watch);
            }
        }
        public void ToggleSelectedWatch(Watch? w){
            List<Watch> toToggle = (w==null)?WatchList.SelectedItems.Cast<Watch>().ToList():new(){w};
            toToggle.ForEach(w=>w.Toggle());
            FilterWatches();
        }

        private void CheckSelectedWatch(Watch? w){
            List<Watch> toCheck = (w==null)?WatchList.SelectedItems.Cast<Watch>().ToList():new(){w};
            toCheck.ForEach(w=>CheckWatch(w));
        }

        private void CheckWatch(Watch w){
            w.Check(()=> {
                if(w.Status==WatchStatus.New && w.Notify) ShowToast(w);
                RefreshList();
            });
        }
        private void ConfigLoad(){
            Watches.Clear();
            if(File.Exists(WatchesConfig))
                Deserialize<List<Watch>>(File.ReadAllText(WatchesConfig))?.ForEach(x=>Watches.Add(x));
            if(File.Exists(AppConfig)){
                SiteWatcherConfig Config = Deserialize<SiteWatcherConfig>(File.ReadAllText(AppConfig))??new SiteWatcherConfig();
                window.Width = Config.WindowSize.X;
                window.Height = Config.WindowSize.Y;
                window.Left = Config.WindowPosition.X;
                window.Top = Config.WindowPosition.Y;
                CheckBrowser.parallelTasks = Math.Max(Config.MaxProcesses,1);
                var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                if(window.Top>b.Height-50 || window.Left>b.Width) window.BringToForeground();

                Tags.Clear();
                Config.Tags.ForEach(t=>Tags.Add(t));
            }
        }

        private void ConfigSave(){
            string newConfig=Serialize(Watches);
            if(newConfig!= oldConfig){
                File.WriteAllText(WatchesConfig,newConfig);
                oldConfig=newConfig;
            }
        }
        private void ConfigSave2(){
            SiteWatcherConfig Config = new SiteWatcherConfig();
            Config.WindowPosition = new((int)window.Left,(int)window.Top);
            Config.WindowSize = new((int)window.Width,(int)window.Height);
            Config.Tags=Tags.ToList();
            Config.MaxProcesses=CheckBrowser.parallelTasks;
            string newConfig2=Serialize(Config);
            if(newConfig2!= oldConfig2){
                File.WriteAllText(AppConfig,newConfig2);
                oldConfig2=newConfig2;
            }
        }
        private void AddWatch(){
            WatchWindow win = new();
            WatchWindowModel model = new(new Watch(),Tags.ToList(), win);
            if(win.ShowDialog()??false){
                Watches.Add(model.Item);
            }
        }
        private void CopyWatch(Watch w){
            Watch n = (Watch)w.Clone();
            n.Checkpoints=new();
            n.LastCheck=DateTime.MinValue;
            WatchWindow win = new();
            WatchWindowModel model = new(n,Tags.ToList(), win);
            if(win.ShowDialog()??false){
                Watches.Add(model.Item);
            }
        }

        private void RefreshIcon(){
            if(Watches.Any(x=>x.Status==WatchStatus.Checking)) IconState = WatchStatus.Checking;
            else if(Watches.Any(x=>x.Status==WatchStatus.Fail && x.Enabled)) IconState = WatchStatus.Fail;
            else if(Watches.Any(x=>x.Status==WatchStatus.New && x.Enabled)) IconState = WatchStatus.New;
            else if(Watches.Any(x=>x.Status==WatchStatus.NoChanges && x.Enabled)) IconState = WatchStatus.NoChanges;
            else IconState = WatchStatus.Off;
        }

        private void FilterWatches(){
            window.Dispatcher.Invoke(()=>{
                TagsUpdating=true;
                if(ShowNew){
                    textFilter=null;
                    window.TagsList.Text="Новые";
                    Watches.ToList().ForEach(w=>{
                        w.IsVisible = w.Status==WatchStatus.New;
                    });
                }else if(textFilter!=null){
                    if(window.TagsList.Text!=textFilter)
                    window.TagsList.Text=textFilter;
                    string lf = textFilter.ToLower();
                    Watches.ToList().ForEach(w=>{
                        w.IsVisible = w.Name.ToLower().Contains(lf) || w.Source.Url.ToLower().Contains(lf);
                    });
                }else{
                    ListWatchTagToStringConverter converter = new();
                    window.TagsList.Text=converter.Convert(Tags,typeof(String),"Все",System.Globalization.CultureInfo.CurrentCulture).ToString();
                    Tags.RaiseListChangedEvents=false;
                    Tags.ToList().ForEach(t=>t.Count=Watches.Where(w=>w.Tags.Where(wt=>wt.Name==t.Name).Count()>0).Count());
                    Tags.RaiseListChangedEvents=true;
                    Watches.Sort((x,y)=>{
                        return y.CompareTo(x);
                    });
                    bool nofilter = !Tags.Any(t=>t.Selected??true);
                    List<string> tagExclude = Tags.Where(t=>t.Selected==null).Select(t=>t.Name).ToList();
                    List<string> tagInclude = Tags.Where(t=>t.Selected??false).Select(t=>t.Name).ToList();
                    Watches.ToList().ForEach(w=>{
                        w.IsVisible = nofilter  || w.Tags.Count==0 && tagInclude.Count==0 ||
                        !w.Tags.Any(wt=>tagExclude.Contains(wt.Name)) 
                        && w.Tags.Any(wt=>tagInclude.Count==0 || tagInclude.Contains(wt.Name));
                    });
                }
                WatchList.Items.Refresh();
                TagsUpdating=false;
            });
        }
        private void RefreshList(){
            ConfigSave();
            FilterWatches();
        }

        private void EditWatch(Watch w){
            WatchWindow win = new();
            WatchWindowModel model = new(w,Tags.ToList(),win);
            if(win.ShowDialog()??false){
                w.CopySettingsFrom(model.Item);
                RefreshList();
            }
        }
        public void CheckpointsWatch(Watch? w){
            if(w==null) return;
            CheckpointsWindow win = new();
            CheckpointsWindowModel model = new(w,win);
            win.Show();
            NavigateWatch(w,false);
        }
    }
}