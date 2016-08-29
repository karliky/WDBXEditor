﻿using WDBXEditor.Reader;
using WDBXEditor.Storage;
using WDBXEditor.Archives.MPQ;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using static WDBXEditor.Common.Constants;
using static WDBXEditor.Forms.InputBox;
using System.Threading.Tasks;
using System.Threading;
using WDBXEditor.Forms;
using WDBXEditor.Common;
using System.Collections.Concurrent;

namespace WDBXEditor
{
    public partial class Main : Form
    {
        protected DBEntry LoadedEntry;

        private BindingSource _bindingsource = new BindingSource();
        private FileSystemWatcher watcher = new FileSystemWatcher();

        private bool isLoaded => (LoadedEntry != null && _bindingsource.DataSource != null);
        private DBEntry getEntry() => Database.Entries.FirstOrDefault(x => x.FileName == txtCurEntry.Text && x.BuildName == txtCurDefinition.Text);
        private string autorun = string.Empty;

        public Main()
        {
            InitializeComponent();

            _bindingsource.DataSource = null;
            advancedDataGridView.DataSource = _bindingsource;
        }

        public Main(string filename)
        {
            InitializeComponent();

            _bindingsource.DataSource = null;
            advancedDataGridView.DataSource = _bindingsource;

            autorun = filename;
        }

        private void Main_Load(object sender, EventArgs e)
        {
            //Create temp directory
            if (!Directory.Exists(TEMP_FOLDER))
                Directory.CreateDirectory(TEMP_FOLDER);

            //Set open dialog filters
            openFileDialog.Filter = string.Join("|", SupportedFileTypes.Select(x => $"{x.Key} ({x.Value})|{x.Value}"));

            //Allow keyboard shortcuts
            Parallel.ForEach(this.Controls.Cast<Control>(), c => c.KeyDown += new KeyEventHandler(KeyDownEvent));

            //Init Find and Replace
            FormHandler.Init(advancedDataGridView);

            //Load definitions
            Task.Run(() => Database.LoadDefinitions())
                .ContinueWith(x => AutoRun(), TaskScheduler.FromCurrentSynchronizationContext());

            //Start FileWatcher
            Watcher();
        }

        private void Main_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (Database.Entries.Count(x => x.Changed) > 0)
                if (MessageBox.Show("You have unsaved changes. Do you wish to exit?", "Unsaved Changes", MessageBoxButtons.YesNo) == DialogResult.No)
                    e.Cancel = true;

            if(!e.Cancel)
            {
                try { File.Delete(TEMP_FOLDER); }
                catch { /*Not really import*/ }

                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                    advancedDataGridView.Dispose();
                    FormHandler.Close();
                }
                catch { /*Just a cleanup exercise*/ }
            }
        }

        private void SetSource(DBEntry dt, bool resetcolumns = true)
        {
            advancedDataGridView.RowHeadersVisible = false;
            advancedDataGridView.ColumnHeadersVisible = false;
            advancedDataGridView.SuspendLayout(); //Performance

            if (dt != null)
            {
                this.Tag = dt.Tag;
                this.Text = $"WDBX Editor - {dt.FileName} {dt.BuildName}";
                LoadedEntry = dt; //Set current table

                if (_bindingsource.IsSorted)
                    _bindingsource.RemoveSort(); //Remove Sort
                 
                if (!string.IsNullOrWhiteSpace(_bindingsource.Filter))
                    _bindingsource.RemoveFilter(); //Remove Filter

                _bindingsource.DataSource = dt.Data; //Change dataset
                _bindingsource.ResetBindings(true);               
                
                columnFilter.Reset(dt.Data.Columns, resetcolumns); //Reset column filter
                wotLKItemFixToolStripMenuItem.Enabled = LoadedEntry.IsFileOf("Item", Expansion.WotLK); //Control WotLK Item Fix

                advancedDataGridView.Columns[LoadedEntry.Key].ReadOnly = true; //Set primary key as readonly
                advancedDataGridView.ClearSelection();
                advancedDataGridView.CurrentCell = advancedDataGridView.Rows[0].Cells[0];
            }
            else
            {
                this.Text = "WDBX Editor";
                this.Tag = string.Empty;
                LoadedEntry = null;

                txtStats.Text = txtCurEntry.Text = txtCurDefinition.Text = "";
                columnFilter.Reset(null, true);
                FormHandler.Close();
            }

            advancedDataGridView.ClearCopyData();
            advancedDataGridView.ClearChanges();
            pasteToolStripMenuItem.Enabled = false;
            undoToolStripMenuItem.Enabled = false;
            redoToolStripMenuItem.Enabled = false;
        }

        #region Data Grid

        private void advancedDataGridView_DataBindingComplete(object sender, DataGridViewBindingCompleteEventArgs e)
        {
            //Reset speed improvements
            advancedDataGridView.RowHeadersVisible = true;
            advancedDataGridView.ColumnHeadersVisible = true;
            advancedDataGridView.ResumeLayout(false);

            ProgressStop();
        }

        private void advancedDataGridView_FilterStringChanged(object sender, EventArgs e)
        {
            _bindingsource.Filter = advancedDataGridView.FilterString;
        }

        private void advancedDataGridView_SortStringChanged(object sender, EventArgs e)
        {
            _bindingsource.Sort = advancedDataGridView.SortString;
        }

        private void advancedDataGridView_CurrentCellChanged(object sender, EventArgs e)
        {
            var cell = advancedDataGridView.CurrentCell;
            txtCurrentCell.Text = $"X: {cell?.ColumnIndex ?? 0}, Y: {cell?.RowIndex ?? 0}";
        }

        private void advancedDataGridView_RowsRemoved(object sender, DataGridViewRowsRemovedEventArgs e)
        {
            if (isLoaded)
                txtStats.Text = $"{LoadedEntry.Data.Columns.Count} fields, {LoadedEntry.Data.Rows.Count} rows";
        }

        private void advancedDataGridView_RowsAdded(object sender, DataGridViewRowsAddedEventArgs e)
        {
            if (isLoaded)
                txtStats.Text = $"{LoadedEntry.Data.Columns.Count} fields, {LoadedEntry.Data.Rows.Count} rows";
        }

        private void columnFilter_ItemCheckChanged(object sender, ItemCheckEventArgs e)
        {
            advancedDataGridView.SetVisible(e.Index, (e.NewValue == CheckState.Checked));
        }

        private void columnFilter_HideEmptyPressed(object sender, EventArgs e)
        {
            if (!isLoaded)
                return;

            foreach (var c in advancedDataGridView.GetEmptyColumns())
                columnFilter.SetItemChecked(c, false);
        }

        private void advancedDataGridView_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (!LoadedEntry.Changed)
            {
                LoadedEntry.Changed = true; //Flag changed datasets
                UpdateListBox();
            }
        }

        private void advancedDataGridView_DefaultValuesNeeded(object sender, DataGridViewRowEventArgs e)
        {
            DefaultRowValues(e.Row.Index);
        }
        #endregion

        #region Data Grid Context
        /// <summary>
        /// Controls the visible context menu (if any) base on the mouse click and element
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void advancedDataGridView_MouseDown(object sender, MouseEventArgs e)
        {
            DataGridView.HitTestInfo info = advancedDataGridView.HitTest(e.X, e.Y);
            if (e.Button == MouseButtons.Right && info.Type == DataGridViewHitTestType.RowHeader)
            {
                advancedDataGridView.SelectRow(info.RowIndex);
                contextMenuStrip.Show(Cursor.Position);
            }
            else if (contextMenuStrip.Visible)
                contextMenuStrip.Hide();
        }

        /// <summary>
        /// Copies the data of the selected row
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void copyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (advancedDataGridView.SelectedRows.Count == 0) return;

            advancedDataGridView.SetCopyData();
            pasteToolStripMenuItem.Enabled = true;
        }

        /// <summary>
        /// Pastes the previously copied row data except the Id
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void pasteToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (advancedDataGridView.SelectedRows.Count == 0) return;

            DataRowView row = ((DataRowView)advancedDataGridView.CurrentRow.DataBoundItem);
            if (row?.Row != null)
            {
                advancedDataGridView.PasteCopyData(row.Row); //Update all fields
            }
            else
            {
                //Force new blank row
                _bindingsource.EndEdit();
                advancedDataGridView.NotifyCurrentCellDirty(true);
                advancedDataGridView.EndEdit();
                advancedDataGridView.NotifyCurrentCellDirty(false);

                //Update new row's data
                row = ((DataRowView)advancedDataGridView.CurrentRow.DataBoundItem);
                if (row?.Row != null)
                    advancedDataGridView.PasteCopyData(row.Row); //Update all fields
            }
        }

        private void gotoIdToolStripMenuItem_Click(object sender, EventArgs e)
        {
            GotoLine();
        }

        private void insertLineToolStripMenuItem_Click(object sender, EventArgs e)
        {
            InsertLine();
        }

        private void clearLineToolStripMenuItem_Click(object sender, EventArgs e)
        {
            DefaultRowValues();
        }

        private void deleteLineToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!isLoaded) return;
            if (advancedDataGridView.SelectedRows.Count == 0) return;

            SendKeys.Send("{delete}");
            LoadedEntry.Changed = true;
            UpdateListBox();
        }
        #endregion

        #region Menu Items

        private void loadFilesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (openFileDialog.ShowDialog(this) == DialogResult.OK)
            {
                using (var loaddefs = new LoadDefinition())
                {
                    loaddefs.Files = openFileDialog.FileNames;
                    if (loaddefs.ShowDialog(this) != DialogResult.OK)
                        return;
                }

                ProgressStart();
                Task.Run(() => Database.LoadFiles(openFileDialog.FileNames))
                .ContinueWith(x =>
                {
                    if (x.Result.Count > 0)
                        new ErrorReport() { Errors = x.Result }.ShowDialog(this);

                    LoadFiles(openFileDialog.FileNames);
                    ProgressStop();

                }, TaskScheduler.FromCurrentSynchronizationContext());
            }

        }

        /// <summary>
        /// Allows the user to select DB* files from an MPQ archive
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void openFromMPQToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var mpq = new LoadMPQ())
            {
                if (mpq.ShowDialog(this) == DialogResult.OK)
                {
                    using (var loaddefs = new LoadDefinition())
                    {
                        loaddefs.Files = mpq.Streams.Keys;
                        if (loaddefs.ShowDialog(this) != DialogResult.OK)
                            return;
                    }

                    ProgressStart();
                    Task.Run(() => Database.LoadFiles(mpq.Streams))
                    .ContinueWith(x =>
                    {
                        if (x.Result.Count > 0)
                            new ErrorReport() { Errors = x.Result }.ShowDialog(this);

                        LoadFiles(mpq.Streams.Keys);
                        ProgressStop();
                    }, TaskScheduler.FromCurrentSynchronizationContext());
                }
            }
        }

        private void openFromCASCToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var mpq = new LoadMPQ())
            {
                mpq.IsMPQ = false;

                if (mpq.ShowDialog(this) == DialogResult.OK)
                {
                    using (var loaddefs = new LoadDefinition())
                    {
                        loaddefs.Files = mpq.FileNames.Values;
                        if (loaddefs.ShowDialog(this) != DialogResult.OK)
                            return;
                    }

                    ProgressStart();
                    Task.Run(() => Database.LoadFiles(mpq.Streams))
                    .ContinueWith(x =>
                    {
                        if (x.Result.Count > 0)
                            new ErrorReport() { Errors = x.Result }.ShowDialog(this);

                        LoadFiles(mpq.Streams.Keys);
                        ProgressStop();
                    }, TaskScheduler.FromCurrentSynchronizationContext());
                }
            }
        }

        private void saveToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!isLoaded) return;
            SaveFile();
        }

        private void saveAllToolStripMenuItem_Click(object sender, EventArgs e)
        {
            SaveAll();
        }

        private void editDefinitionsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            new EditDefinition().ShowDialog(this);
        }

        private void findToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Find();
        }

        private void replaceToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Replace();
        }

        private void reloadToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Reload();
        }

        private void closeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CloseFile();
        }

        private void closeAllToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_bindingsource.Filter))
                _bindingsource.RemoveFilter();

            if (_bindingsource.IsSorted)
                _bindingsource.RemoveSort();

            for (int i = 0; i < Database.Entries.Count; i++)
                Database.Entries[i].Dispose();
            Database.Entries.Clear();
            Database.Entries.TrimExcess();

            SetSource(null);
            UpdateListBox();
        }

        private void undoToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Undo();
        }

        private void redoToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Redo();
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            new About().ShowDialog(this);
        }

        private void helpToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            Help.ShowHelp(this, Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "Help.chm"));
        }

        private void insertToolStripMenuItem_Click(object sender, EventArgs e)
        {
            InsertLine();
        }

        private void newLineToolStripMenuItem_Click(object sender, EventArgs e)
        {
            NewLine();
        }

        #endregion

        #region Export Menu Items
        /// <summary>
        /// Exports the current dataset directly into sql
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void toSQLToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!isLoaded) return;

            using (var sql = new LoadSQL() { Entry = LoadedEntry, ConnectionOnly = true })
            {
                if (sql.ShowDialog(this) == DialogResult.OK)
                {
                    ProgressStart();
                    Task.Factory.StartNew(() => { LoadedEntry.ToSqlTable(sql.ConnectionString); })
                    .ContinueWith(x =>
                    {
                        if (x.IsFaulted)
                            MessageBox.Show("An error occured exporting to SQL.");
                        else
                            MessageBox.Show("Sucessfully exported to SQL.");

                        ProgressStop();
                    }, TaskScheduler.FromCurrentSynchronizationContext());
                }
            }
        }

        /// <summary>
        /// Exports the current dataset to a MySQL SQL file
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void toSQLFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!isLoaded) return;

            using (var sfd = new SaveFileDialog() { FileName = LoadedEntry.TableStructure.Name + ".sql", Filter = "SQL Files|*.sql" })
            {
                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    ProgressStart();
                    Task.Factory.StartNew(() =>
                    {
                        using (FileStream fs = new FileStream(sfd.FileName, FileMode.Create))
                        {
                            string sql = LoadedEntry.ToSql();
                            byte[] data = Encoding.UTF8.GetBytes(sql);
                            fs.Write(data, 0, data.Length);
                        }
                    })
                    .ContinueWith(x =>
                    {
                        ProgressStop();

                        if (x.IsFaulted)
                            MessageBox.Show($"Error generating SQL file {x.Exception.Message}");
                        else
                            MessageBox.Show($"File successfully exported to {sfd.FileName}");

                    }, TaskScheduler.FromCurrentSynchronizationContext());
                }
            }
        }

        /// <summary>
        /// Exports the current dataset to a CSV/Txt file
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void toCSVToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!isLoaded) return;

            using (var sfd = new SaveFileDialog())
            {
                sfd.FileName = LoadedEntry.TableStructure.Name + ".csv";
                sfd.Filter = "CSV files (*.csv)|*.csv|Text files (*.txt)|*.txt";

                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    ProgressStart();
                    Task.Factory.StartNew(() =>
                    {
                        using (FileStream fs = new FileStream(sfd.FileName, FileMode.Create))
                        {
                            string sql = LoadedEntry.ToCsv();
                            byte[] data = Encoding.UTF8.GetBytes(sql);
                            fs.Write(data, 0, data.Length);
                        }
                    })
                    .ContinueWith(x =>
                    {
                        ProgressStop();

                        if (x.IsFaulted)
                            MessageBox.Show($"Error generating CSV file {x.Exception.Message}");
                        else
                            MessageBox.Show($"File successfully exported to {sfd.FileName}");

                    }, TaskScheduler.FromCurrentSynchronizationContext());
                }
            }
        }

        /// <summary>
        /// Exports the current dataset to a MPQ file
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void toMPQToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!isLoaded) return;

            MpqArchiveVersion version = MpqArchiveVersion.Version2;
            if (LoadedEntry.Build <= (int)ExpansionFinalBuild.WotLK)
                version = MpqArchiveVersion.Version2;
            else if (LoadedEntry.Build <= (int)ExpansionFinalBuild.MoP)
                version = MpqArchiveVersion.Version4;
            else
            {
                MessageBox.Show("Only clients before WoD support MPQ archives.");
                return;
            }

            //Get the correct save settings
            using (var sfd = new SaveFileDialog())
            {
                sfd.InitialDirectory = Path.GetDirectoryName(LoadedEntry.FilePath);
                sfd.OverwritePrompt = false;
                sfd.CheckFileExists = false;

                //Set the correct filter
                switch (Path.GetExtension(LoadedEntry.FilePath).ToLower().TrimStart('.'))
                {
                    case "dbc":
                    case "db2":
                        sfd.FileName = LoadedEntry.TableStructure.Name + ".mpq";
                        sfd.Filter = "MPQ Files|*.mpq";
                        break;
                    default:
                        MessageBox.Show("Only DBC and DB2 files can be saved to MPQ.");
                        return;
                }

                if (sfd.ShowDialog(this) == DialogResult.OK)
                    LoadedEntry.ToMPQ(sfd.FileName, version);
            }
        }

        #endregion

        #region Import Menu Items
        /// <summary>
        /// Import data rows from a CSV/Txt File.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void fromCSVToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!isLoaded) return;

            using (var loadCsv = new LoadCSV() { Entry = LoadedEntry })
            {
                switch (loadCsv.ShowDialog(this))
                {
                    case DialogResult.OK:
                        SetSource(getEntry(), false);
                        ProgressStop();
                        MessageBox.Show("CSV import succeeded.");
                        break;
                    case DialogResult.Abort:
                        ProgressStop();
                        if (!string.IsNullOrWhiteSpace(loadCsv.ErrorMessage))
                            MessageBox.Show("CSV import failed: " + loadCsv.ErrorMessage);
                        else
                            MessageBox.Show("CSV import failed due to incorrect file format.");
                        break;
                }
            }
        }

        /// <summary>
        /// Import data rows from SQL database
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void fromSQLToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!isLoaded) return;

            using (var importSql = new LoadSQL() { Entry = LoadedEntry })
            {
                switch (importSql.ShowDialog(this))
                {
                    case DialogResult.OK:
                        SetSource(getEntry(), false);
                        MessageBox.Show("SQL import succeeded.");
                        break;
                    case DialogResult.Abort:
                        if (!string.IsNullOrWhiteSpace(importSql.ErrorMessage))
                            MessageBox.Show(importSql.ErrorMessage);
                        else
                            MessageBox.Show("SQL import failed due to incorrect file format.");
                        break;
                }
            }
        }

        #endregion

        #region Tool Menu Items
        /// <summary>
        /// Loads Item.dbc rows from item_template database table
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void wotLKItemFixToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var itemfix = new WotLKItemFix())
            {
                itemfix.Entry = LoadedEntry;
                if (itemfix.ShowDialog(this) == DialogResult.OK)
                    SetSource(LoadedEntry);
            }
        }

        private void legionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            new WD5Parser().ShowDialog(this);
        }
        #endregion

        #region File ListView
        private void closeToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            DBEntry selection = (DBEntry)((DataRowView)lbFiles.SelectedItem)["Key"];
            if (LoadedEntry == selection)
                CloseFile();
            else
            {
                Database.Entries.Remove(selection);
                Database.Entries.TrimExcess();
                UpdateListBox();
            }
        }

        private void editToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            DBEntry selection = (DBEntry)((DataRowView)lbFiles.SelectedItem)["Key"];
            if (LoadedEntry != selection)
                SetSource(selection);
        }

        /// <summary>
        /// Show the context menu at the right position on right click
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void lbFiles_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                int index = lbFiles.IndexFromPoint(e.Location);
                if (index != ListBox.NoMatches)
                {
                    lbFiles.SelectedIndex = index;
                    filecontextMenuStrip.Show(Cursor.Position);
                }
            }
        }

        /// <summary>
        /// Changes the current dataset
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void lbFiles_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            int index = lbFiles.IndexFromPoint(e.Location);
            if (index != ListBox.NoMatches)
            {
                DBEntry entry = (DBEntry)((DataRowView)lbFiles.Items[index])["Key"];
                txtCurEntry.Text = entry.FileName;
                txtCurDefinition.Text = entry.BuildName;
                txtStats.Text = $"{entry.Data.Columns.Count} fields, {entry.Data.Rows.Count} rows";

                SetSource(getEntry());
            }
        }
        #endregion

        #region Command Actions
        private void LoadFiles(IEnumerable<string> fileNames)
        {
            //Update the DB list, remove old and add new
            UpdateListBox();

            if (lbFiles.Items.Count == 0)
                return;

            //Refresh the data if the file was reloaded
            if (LoadedEntry != null && fileNames.Any(x => x.Equals(LoadedEntry.FileName, StringComparison.CurrentCultureIgnoreCase)))
            {
                var entry = (DBEntry)lbFiles.SelectedValue;                
                txtCurEntry.Text = entry.FileName;
                txtCurDefinition.Text = entry.BuildName;
                txtStats.Text = $"{entry.Data.Columns.Count} fields, {entry.Data.Rows.Count} rows";

                SetSource(getEntry());
            }

            //Current file is no longer open
            if (getEntry() == null)
            {
                LoadedEntry = null;
                SetSource(null);
            }

            //Load the first item if no data exists
            if (LoadedEntry == null && lbFiles.Items.Count > 0)
            {
                lbFiles.SetSelected(0, true);

                var entry = (DBEntry)lbFiles.SelectedValue;
                txtCurEntry.Text = entry.FileName;
                txtCurDefinition.Text = entry.BuildName;
                txtStats.Text = $"{entry.Data.Columns.Count} fields, {entry.Data.Rows.Count} rows";

                SetSource(getEntry());
            }

            //Preset options
            if (LoadedEntry != null)
                txtCurDefinition.Text = LoadedEntry.BuildName;
        }

        /// <summary>
        /// Provides a SaveFileDialog to save the current file
        /// </summary>
        private void SaveFile()
        {
            if (!isLoaded) return;

            //Get the correct save settings
            using (var sfd = new SaveFileDialog())
            {
                sfd.InitialDirectory = Path.GetDirectoryName(LoadedEntry.FilePath);
                sfd.FileName = LoadedEntry.FileName;

                //Set the correct filter
                switch (Path.GetExtension(LoadedEntry.FilePath).ToLower().TrimStart('.'))
                {
                    case "dbc":
                        sfd.FileName = LoadedEntry.TableStructure.Name + ".dbc";
                        sfd.Filter = "DBC Files|*.dbc";
                        break;
                    case "db2":
                        sfd.FileName = LoadedEntry.TableStructure.Name + ".db2";
                        sfd.Filter = "DB2 Files|*.db2";
                        break;
                    case "adb":
                        sfd.FileName = LoadedEntry.TableStructure.Name + ".adb";
                        sfd.Filter = "ADB Files|*.adb";
                        break;
                    case "wdb":
                        MessageBox.Show("Saving is not implemented for WDB files.");
                        return;
                }

                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    ProgressStart();
                    Task.Factory.StartNew(() =>
                    {
                        new DBReader().Write(LoadedEntry, sfd.FileName);
                    })
                    .ContinueWith(x =>
                    {
                        ProgressStop();
                        LoadedEntry.Changed = false;
                        UpdateListBox();

                        if (x.IsFaulted)
                        {
                            MessageBox.Show($"Error exporting to file {x.Exception.Message}");
                            return;
                        }
                    }, TaskScheduler.FromCurrentSynchronizationContext());
                }
            }
        }

        /// <summary>
        /// Provides a SaveFolderDialog to bulk save all open files
        /// </summary>
        private void SaveAll()
        {
            using (var fbd = new FolderBrowserDialog())
            {
                if (fbd.ShowDialog(this) == DialogResult.OK)
                {
                    ProgressStart();

                    Task.Run(() => Database.SaveFiles(fbd.SelectedPath))
                        .ContinueWith(x =>
                        {
                            if (x.Result.Count > 0)
                                new ErrorReport() { Errors = x.Result }.ShowDialog(this);

                            ProgressStop();
                        }, TaskScheduler.FromCurrentSynchronizationContext());
                }
            }
        }

        private void GotoLine()
        {
            if (!isLoaded) return;

            int id = 0;
            string res = "";
            if (ShowInputDialog("Id:", "Go to Id", 0.ToString(), ref res) == DialogResult.OK)
            {
                if (int.TryParse(res, out id)) //Ensure the result is an integer
                {
                    int index = _bindingsource.Find(LoadedEntry.Key, id); //See if the Id exists
                    if (index >= 0)
                        advancedDataGridView.SelectRow(index);
                    else
                        MessageBox.Show($"Id {id} doesn't exist.");
                }
                else
                    MessageBox.Show($"Invalid Id.");
            }
        }

        private void Find()
        {
            if (isLoaded)
                FormHandler.ShowReplaceForm(false);
        }

        private void Replace()
        {
            if (isLoaded)
                FormHandler.ShowReplaceForm(true);
        }

        private void Reload()
        {
            ProgressStart();
            Task.Run(() => Database.LoadFiles(new string[] { LoadedEntry.FilePath }))
            .ContinueWith(x =>
            {
                if (x.Result.Count > 0)
                    new ErrorReport() { Errors = x.Result }.ShowDialog(this);

                LoadFiles(openFileDialog.FileNames);
                ProgressStop();

            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void CloseFile()
        {
            if (!string.IsNullOrWhiteSpace(_bindingsource.Filter))
                _bindingsource.RemoveFilter();

            if (_bindingsource.IsSorted)
                _bindingsource.RemoveSort();

            if (LoadedEntry != null)
            {
                LoadedEntry.Dispose();
                Database.Entries.Remove(LoadedEntry);
                Database.Entries.TrimExcess();
            }

            SetSource(null);
            UpdateListBox();
        }

        private void Undo()
        {
            advancedDataGridView.Undo();            
        }

        private void Redo()
        {
            advancedDataGridView.Redo();
        }

        private void InsertLine()
        {
            if (!isLoaded) return;

            int id = 0;
            string res = "";
            if (ShowInputDialog("Id:", "Id to insert", 0.ToString(), ref res) == DialogResult.OK)
            {
                if (int.TryParse(res, out id)) //Ensure the result is an integer
                {
                    int index = _bindingsource.Find(LoadedEntry.Key, id); //See if the Id exists
                    if (index < 0)
                    {
                        index = NewLine();
                        advancedDataGridView.Rows[index].Cells[LoadedEntry.Key].Value = id;
                        DefaultRowValues(index);

                        advancedDataGridView.OnUserAddedRow(advancedDataGridView.Rows[index]);

                        LoadedEntry.Changed = true;
                        UpdateListBox();
                    }

                    advancedDataGridView.SelectRow(index);
                }
                else
                    MessageBox.Show($"Invalid Id.");
            }
        }

        private void DefaultRowValues(int index = -1)
        {
            if (!isLoaded)
                return;

            if (advancedDataGridView.SelectedRows.Count == 1)
                index = advancedDataGridView.CurrentRow.Index;
            else if (index == -1)
                return;

            for (int i = 0; i < advancedDataGridView.Columns.Count; i++)
            {
                if (advancedDataGridView.Columns[i].Name == LoadedEntry.Key)
                    continue;

                advancedDataGridView.Rows[index].Cells[i].Value = advancedDataGridView.Columns[i].ValueType.GetDefaultValue();

                LoadedEntry.Changed = true;
                UpdateListBox();
            }
        }

        private int NewLine()
        {
            if (!isLoaded) return 0;

            var row = LoadedEntry.Data.NewRow();
            LoadedEntry.Data.Rows.Add(row);
            int index = _bindingsource.Find(LoadedEntry.Key, row[LoadedEntry.Key]);
            DefaultRowValues(index);
            advancedDataGridView.SelectRow(index);
            return index;
        }
        #endregion

        #region File Filter
        private void LoadBuilds()
        {
            var tables = lbFiles.Items.Cast<DataRowView>()
                            .Select(x => ((DBEntry)x["Key"]).TableStructure)
                            .OrderBy(x => x.Build)
                            .Select(x => x.BuildText).Distinct();

            cbBuild.Items.Clear();
            cbBuild.Items.Add("");
            cbBuild.Items.AddRange(tables.ToArray());
        }

        private void txtFilter_TextChanged(object sender, EventArgs e)
        {
            ((BindingSource)lbFiles.DataSource).Filter = $"([Value] LIKE '%{txtFilter.Text}%') AND [Value] LIKE '%{cbBuild.Text}%'";
        }

        private void cbBuild_SelectedIndexChanged(object sender, EventArgs e)
        {
            ((BindingSource)lbFiles.DataSource).Filter = $"([Value] LIKE '%{txtFilter.Text}%') AND [Value] LIKE '%{cbBuild.Text}%'";
        }

        private void btnReset_Click(object sender, EventArgs e)
        {
            txtFilter.Text = "";
            cbBuild.Text = "";
        }
        #endregion


        private void UpdateListBox()
        {
            //Update the DB list, remove old and add new
            DataTable dt = new DataTable();
            dt.Columns.Add("Key", typeof(DBEntry));
            dt.Columns.Add("Value", typeof(string));

            var entries = Database.Entries.OrderBy(x => x.Build).ThenBy(x => x.FileName);
            foreach (var entry in entries)
                dt.Rows.Add(entry, $"{entry.FileName} - {entry.BuildName}{(entry.Changed ? "*" : "")}");

            lbFiles.BeginUpdate();
            lbFiles.DataSource = new BindingSource(dt, null);

            if (Database.Entries.Count > 0)
            {
                lbFiles.ValueMember = "Key";
                lbFiles.DisplayMember = "Value";
            }
            else
            {
                ((BindingSource)lbFiles.DataSource).DataSource = null;
                ((BindingSource)lbFiles.DataSource).Clear();
            }

            lbFiles.EndUpdate();

            LoadBuilds();
        }

        private void KeyDownEvent(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.S) //Save
                SaveFile();
            else if (e.Control && e.KeyCode == Keys.G) //Goto
                GotoLine();
            else if (e.Control && e.Shift && e.KeyCode == Keys.S) //Save All
                SaveAll();
            else if (e.Control && e.KeyCode == Keys.F) //Find
                Find();
            else if (e.Control && e.KeyCode == Keys.H) //Replace
                Replace();
            else if (e.Control && e.KeyCode == Keys.R) //Reload
                Reload();
            else if (e.Control && e.KeyCode == Keys.W) //Close
                CloseFile();
            else if (e.Control && e.KeyCode == Keys.Z) //Undo
                Undo();
            else if (e.Control && e.KeyCode == Keys.Y) //Redo
                Redo();
            else if (e.Control && e.KeyCode == Keys.N) //Newline
                NewLine();
            else if (e.Control && e.KeyCode == Keys.I) //Insert
                InsertLine();
        }

        public void ProgressStart()
        {
            progressBar.Start();
            menuStrip.Enabled = false;
            columnFilter.Enabled = false;
            gbSettings.Enabled = false;
            gbFilter.Enabled = false;
            advancedDataGridView.ReadOnly = true;
        }

        private void ProgressStop()
        {
            progressBar.Stop();
            progressBar.Value = 0;
            menuStrip.Enabled = true;
            columnFilter.Enabled = true;
            gbSettings.Enabled = true;
            gbFilter.Enabled = true;
            advancedDataGridView.ReadOnly = false;
        }

        private void AutoRun()
        {
            if (File.Exists(autorun))
            {
                string[] FileNames = new string[] { autorun };
                using (var loaddefs = new LoadDefinition())
                {
                    loaddefs.Files = FileNames;
                    if (loaddefs.ShowDialog(this) != DialogResult.OK)
                        return;
                }

                ProgressStart();
                Task.Run(() => Database.LoadFiles(FileNames))
                .ContinueWith(x =>
                {
                    if (x.Result.Count > 0)
                        new ErrorReport() { Errors = x.Result }.ShowDialog(this);

                    LoadFiles(FileNames);
                    ProgressStop();

                }, TaskScheduler.FromCurrentSynchronizationContext());
            }

            autorun = string.Empty;
        }

        private void Watcher()
        {
            watcher = new FileSystemWatcher();
            watcher.Path = Path.GetDirectoryName(DEFINITION_DIR);
            watcher.NotifyFilter = NotifyFilters.LastWrite;
            watcher.Filter = "*.xml";
            watcher.EnableRaisingEvents = true;
            watcher.Changed += delegate { Task.Run(() => Database.LoadDefinitions()); };
        }

        private void advancedDataGridView_UndoRedoChanged(object sender, EventArgs e)
        {
            undoToolStripMenuItem.Enabled = advancedDataGridView.CanUndo;
            redoToolStripMenuItem.Enabled = advancedDataGridView.CanRedo;
        }
    }
}