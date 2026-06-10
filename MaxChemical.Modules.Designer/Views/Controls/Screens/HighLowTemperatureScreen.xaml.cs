using DevicePlugins.Devices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace MaxChemical.Modules.Designer.Views.Controls.Screens
{
    /// <summary>
    /// HighLowTemperature.xaml 的交互逻辑
    /// </summary>
    public partial class HighLowTemperature : DeviceScreenBase
    {
        private bool _animationRuning;
        private readonly Storyboard _storyboard;

        public HighLowTemperature()
        {
            InitializeComponent();

            _storyboard = new Storyboard();
            ApplyRunAnimation();
        }

        protected override string PollCommandName => "获取可视化数据";

        protected override void OnDataRefreshed(DeviceParameters output)
        {
            // 物料温度
            var value = output.GetNullableValue<double>("MaterialTemperature");
            TbMaterial.Text = value.HasValue ? value.Value.ToString("F2") : "--";
            // 出口温度
            value = output.GetNullableValue<double>("OutletTemperature");
            TbOutlet.Text = value.HasValue ? value.Value.ToString("F2") : "--";
            // 回口温度
            value = output.GetNullableValue<double>("BackTemperature");
            TbBack.Text = value.HasValue ? value.Value.ToString("F2") : "--";
            // 温度设定值
            value = output.GetNullableValue<double>("SV");
            if (!TbSetSv.IsKeyboardFocusWithin)
            {
                TbSetSv.Text = value.HasValue ? value.Value.ToString("F2") : "--";
            }

            var run = output.GetNullableValue<bool>("Run");
            if (run.HasValue && run.Value)
            {
                BtnRunStop.Content = "停止";
                if (!_animationRuning)
                {
                    _storyboard.Begin(this, true);
                    _animationRuning = true;
                }
            }
            else
            {
                BtnRunStop.Content = "运行";

                _storyboard.Remove(this);
                CircleRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                EllipseRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                _animationRuning = false;
            }
        }

        protected override void OnPollError(Exception ex)
        {
            TbMaterial.Text = "--";
            TbOutlet.Text = "--";
            TbBack.Text = "--";
            if (!TbSetSv.IsKeyboardFocusWithin)
            {
                TbSetSv.Text = "--";
            }
        }

        /// <summary>
        /// 温度设定值文本框按键响应事件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void TbSetSv_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }
            if (!float.TryParse(TbSetSv.Text, out float value))
            {
                TbSetSv.Text = "";
                return;
            }
            var input = new Dictionary<string, object>()
            {
                ["TargetValue"] = value
            };
            await ExecuteCommandAsync("设置温度SV", input);

            // 清除文本框焦点
            Keyboard.ClearFocus();
        }


        private async void BtnRunStop_Click(object sender, RoutedEventArgs e)
        {
            if (BtnRunStop.Content.ToString() == "运行")
            {
                var input = new Dictionary<string, object>()
                {
                    ["TargetValue"] = true
                };
                if (await ExecuteCommandAsync("定值启动/停止", input))
                {
                    BtnRunStop.Content = "停止";

                    _storyboard.Begin(this, true);
                    _animationRuning = true;
                }
                return;
            }


            if (BtnRunStop.Content.ToString() == "停止")
            {
                var input = new Dictionary<string, object>()
                {
                    ["TargetValue"] = false
                };
                if (await ExecuteCommandAsync("定值启动/停止", input))
                {
                    BtnRunStop.Content = "运行";
                    _storyboard.Remove(this);
                    CircleRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                    EllipseRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                    _animationRuning = false;
                }
            }
        }

        private void ApplyRunAnimation()
        {
            this.RegisterName("CircleRotate", CircleRotate);
            this.RegisterName("EllipseRotate", EllipseRotate);

            DoubleAnimationUsingKeyFrames circleAnimation = new DoubleAnimationUsingKeyFrames();
            circleAnimation.KeyFrames.Add(new EasingDoubleKeyFrame()
            {
                Value = 360,
                KeyTime = TimeSpan.FromSeconds(2)
            });
            circleAnimation.RepeatBehavior = RepeatBehavior.Forever;

            DoubleAnimationUsingKeyFrames ellipseAnimation = new DoubleAnimationUsingKeyFrames();
            ellipseAnimation.KeyFrames.Add(new EasingDoubleKeyFrame()
            {
                Value = 390,
                KeyTime = TimeSpan.FromSeconds(2)
            });
            ellipseAnimation.RepeatBehavior = RepeatBehavior.Forever;


            Storyboard.SetTargetName(circleAnimation, "CircleRotate");
            Storyboard.SetTargetProperty(circleAnimation, new PropertyPath("Angle"));

            Storyboard.SetTargetName(ellipseAnimation, "EllipseRotate");
            Storyboard.SetTargetProperty(ellipseAnimation, new PropertyPath("Angle"));

            _storyboard.Children.Add(circleAnimation);
            _storyboard.Children.Add(ellipseAnimation);
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (BtnOnOff.Content.ToString() == "ON")
            {
                
            }
            else
            {
                
            }
        }
    }
}
