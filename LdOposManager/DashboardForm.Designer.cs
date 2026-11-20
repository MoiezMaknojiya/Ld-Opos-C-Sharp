namespace LdOposManager
{
    partial class DashboardForm
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(DashboardForm));
            lblWelcome = new Label();
            btnLogout = new Button();
            lblScannerStatus = new Label();
            btnChangeToOposMode = new Button();
            btnUpdateToHidKeyboard = new Button();
            SuspendLayout();
            // 
            // lblWelcome
            // 
            lblWelcome.AutoSize = true;
            lblWelcome.Location = new Point(15, 24);
            lblWelcome.Name = "lblWelcome";
            lblWelcome.Size = new Size(57, 15);
            lblWelcome.TabIndex = 0;
            lblWelcome.Text = "Welcome";
            // 
            // btnLogout
            // 
            btnLogout.Font = new Font("Segoe UI", 10F);
            btnLogout.Location = new Point(204, 14);
            btnLogout.Name = "btnLogout";
            btnLogout.Size = new Size(105, 33);
            btnLogout.TabIndex = 1;
            btnLogout.Text = "Logout";
            btnLogout.UseVisualStyleBackColor = true;
            btnLogout.Click += btnLogout_Click;
            // 
            // lblScannerStatus
            // 
            lblScannerStatus.AutoSize = true;
            lblScannerStatus.Location = new Point(15, 45);
            lblScannerStatus.Name = "lblScannerStatus";
            lblScannerStatus.Size = new Size(0, 15);
            lblScannerStatus.TabIndex = 2;
            // 
            // btnChangeToOposMode
            // 
            btnChangeToOposMode.Font = new Font("Segoe UI", 10F);
            btnChangeToOposMode.Location = new Point(15, 105);
            btnChangeToOposMode.Name = "btnChangeToOposMode";
            btnChangeToOposMode.Size = new Size(294, 33);
            btnChangeToOposMode.TabIndex = 3;
            btnChangeToOposMode.Text = "Change to OPOS Mode";
            btnChangeToOposMode.UseVisualStyleBackColor = true;
            btnChangeToOposMode.Click += btnChangeToOposMode_Click;
            // 
            // btnUpdateToHidKeyboard
            // 
            btnUpdateToHidKeyboard.Font = new Font("Segoe UI", 10F);
            btnUpdateToHidKeyboard.Location = new Point(15, 144);
            btnUpdateToHidKeyboard.Name = "btnUpdateToHidKeyboard";
            btnUpdateToHidKeyboard.Size = new Size(294, 33);
            btnUpdateToHidKeyboard.TabIndex = 4;
            btnUpdateToHidKeyboard.Text = "Update to HID-Keyboard Mode";
            btnUpdateToHidKeyboard.UseVisualStyleBackColor = true;
            btnUpdateToHidKeyboard.Click += btnUpdateToHidKeyboard_Click;
            // 
            // DashboardForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(321, 192);
            Controls.Add(btnUpdateToHidKeyboard);
            Controls.Add(btnChangeToOposMode);
            Controls.Add(lblScannerStatus);
            Controls.Add(btnLogout);
            Controls.Add(lblWelcome);
            Icon = (Icon)resources.GetObject("$this.Icon");
            Name = "DashboardForm";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "Dashboard";
            Load += DashboardForm_Load;
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Label lblWelcome;
        private Button btnLogout;
        private Label lblScannerStatus;
        private Button btnChangeToOposMode;
        private Button btnUpdateToHidKeyboard;
    }
}
