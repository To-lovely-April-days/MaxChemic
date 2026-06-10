// ============================================================================
//  IDeviceScreen.cs  （注册表模式版本，无 WPF 依赖）
//  放置位置：DevicePlugins/Devices/IDeviceScreen.cs
//  命名空间：DevicePlugins.Devices
//
//  作用：
//    设备“嵌入式屏幕”的可选能力接口。设备只声明“我需要哪一块屏幕”（一个字符串
//    key），完全不引用任何 WPF 类型，也不 new 任何 Designer 里的控件。
//    具体屏幕控件的创建由 Designer 侧的 DeviceScreenRegistry 按 key 负责。
//
//  依赖方向：DevicePlugins 不依赖 Designer（彻底消除循环引用）。
//            Designer -> DevicePlugins（单向，正常分层）。
//
//  和现有 DeviceToControlConverter 的 "CUSTOM_CONTROL:xxx" 思路一致：
//    用字符串 key 解耦“是谁”和“怎么创建”。
// ============================================================================

namespace DevicePlugins.Devices
{
    /// <summary>
    /// 设备嵌入式屏幕能力接口（可选实现）。
    /// 设备实现此接口表示“我有一块嵌入式屏幕”，并通过 ScreenKey
    /// 告诉宿主该用哪个屏幕控件。屏幕控件本身由 Designer 侧注册表创建。
    /// </summary>
    public interface IDeviceScreen
    {
        /// <summary>
        /// 屏幕标识。Designer 侧 DeviceScreenRegistry 用它查到对应的屏幕工厂。
        /// 例如两液一气进料系统返回 "TwoLiquidOneGasFeed"。
        /// 约定：全局唯一、稳定不变（改名会导致注册表查不到）。
        /// </summary>
        string ScreenKey { get; }
    }
}