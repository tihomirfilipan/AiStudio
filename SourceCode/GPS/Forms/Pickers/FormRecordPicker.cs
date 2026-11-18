using AgLibrary.Logging;
using AgOpenGPS.Core.Translations;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace AgOpenGPS.Forms.Pickers
{
    public partial class FormRecordPicker : Form
    {
        private readonly FormGPS mf = null;

        private readonly List<string> fileList = new List<string>();

        public FormRecordPicker(Form callingForm)
        {
            //get copy of the calling main form
            mf = callingForm as FormGPS;

            InitializeComponent();
            //translate all the controls
            this.Text = gStr.gsRecordedPathPicker;
            buttonOpenExistingLv.Text = gStr.gsUseSelected;
            labelCancel.Text = gStr.gsCancel;
            labelDeleteRecord.Text = gStr.gsDelete;
            labelPathOff.Text = gStr.gsTurnOffRecordedPath;
        }

        private void FormRecordPicker_Load(object sender, EventArgs e)
        {
            LoadList();
        }

        private void LoadList()
        {
            ListViewItem itm;

            string fieldDir = Path.Combine(RegistrySettings.fieldsDirectory, mf.currentFieldDirectory);

            string[] files = Directory.GetFiles(fieldDir);

            fileList?.Clear();
            lvLines.Items.Clear();

            // Here we use the filename of all .rec files in the current field dir.
            // The path and postfix is stripped off.

            foreach (string file in files)
            {
                if (file.EndsWith(".rec"))
                {
                    string recordName = Path.GetFileNameWithoutExtension(file);
                    itm = new ListViewItem(recordName);
                    lvLines.Items.Add(itm);
                }
            }

            if (lvLines.Items.Count == 0)
            {
                FormDialog.Show(
                    "No Recorded Paths",
                    "Create A Path First",
                    DialogSeverity.Error);

                Close();
            }
        }

        private void btnOpenExistingLv_Click(object sender, EventArgs e)
        {
            int count = lvLines.SelectedItems.Count;
            if (count > 0)
            {
                string selectedRecord = lvLines.SelectedItems[0].SubItems[0].Text;
                string selectedRecordPath = Path.Combine(RegistrySettings.fieldsDirectory, mf.currentFieldDirectory, selectedRecord + ".rec");

                // a path that is being driven is stopped first: its position index does not
                // belong to the path loaded now (index past the end of a shorter path)
                mf.recPath.StopDrivingRecordedPath();
                mf.recPath.currentPositonIndex = 0;
                mf.recPath.resumeState = 0;

                if (!File.Exists(selectedRecordPath))
                {
                    FormDialog.Show(gStr.gsRecordedPathFileIsCorrupt, selectedRecord, DialogSeverity.Error);
                    return;
                }

                try
                {
                    // read the whole file first, so a damaged file leaves the current path as it is
                    var loaded = new List<CRecPathPt>();
                    using (StreamReader reader = new StreamReader(selectedRecordPath))
                    {
                        reader.ReadLine();                      // header
                        int numPoints = int.Parse((reader.ReadLine() ?? "0").Trim(), CultureInfo.InvariantCulture);
                        for (int v = 0; v < numPoints; v++)
                        {
                            string line = reader.ReadLine();
                            if (line == null) break;
                            string[] words = line.Split(',');
                            if (words.Length < 5) continue;
                            loaded.Add(new CRecPathPt(
                                double.Parse(words[0], CultureInfo.InvariantCulture),
                                double.Parse(words[1], CultureInfo.InvariantCulture),
                                double.Parse(words[2], CultureInfo.InvariantCulture),
                                double.Parse(words[3], CultureInfo.InvariantCulture),
                                bool.Parse(words[4].Trim())));
                        }
                    }

                    mf.recPath.recList.Clear();
                    mf.recPath.recList.AddRange(loaded);

                    // the selected path becomes the active one of the field (loaded again when the
                    // field is opened)
                    mf.FileSaveRecPath();
                }
                catch (Exception ex)
                {
                    FormDialog.Show(gStr.gsRecordedPathFileIsCorrupt, gStr.gsButFieldIsLoaded, DialogSeverity.Error);
                    Log.EventWriter("Load Recorded Path" + ex.ToString());
                }
            }
        }

        private void btnDeleteField_Click(object sender, EventArgs e)
        {
            int count = lvLines.SelectedItems.Count;
            string dir2Delete;
            if (count > 0)
            {
                string selectedRecord = lvLines.SelectedItems[0].SubItems[0].Text;
                dir2Delete = Path.Combine(RegistrySettings.fieldsDirectory, mf.currentFieldDirectory, selectedRecord + ".rec");

                // Ask confirmation before deleting the file
                DialogResult result = FormDialog.ShowQuestion(
                    gStr.gsDeleteForSure,
                    dir2Delete);

                if (result == DialogResult.OK)
                {
                    System.IO.File.Delete(dir2Delete);
                }
                else return;

            }
            else return;

            LoadList();
        }

        private void btnTurnOffRecPath_Click(object sender, EventArgs e)
        {
            mf.recPath.StopDrivingRecordedPath();
            mf.recPath.recList.Clear();
            mf.FileSaveRecPath();
            mf.panelDrag.Visible = false;
            Close();
        }
    }
}