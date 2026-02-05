"use client";

import { useAuth } from '@/contexts/AuthContext';
import { useNotifications } from '@/contexts/NotificationContext';
import { useState, useRef, useEffect } from 'react';
import Link from 'next/link';
import { API_BASE_URL } from '@/lib/config';

export default function Navbar() {
  const { isAuthenticated, user, logout } = useAuth();
  const { notifications, unreadCount, markAsRead, markAllAsRead, removeNotification, clearAll } = useNotifications();
  const [showNotifications, setShowNotifications] = useState(false);
  const [showUserMenu, setShowUserMenu] = useState(false);
  const [showSettings, setShowSettings] = useState(false);
  const [adminMode, setAdminMode] = useState(() => {
    if (typeof window !== 'undefined') {
      return localStorage.getItem('adminMode') === 'true';
    }
    return false;
  });
  const [galacticAdminMode, setGalacticAdminMode] = useState(() => {
    if (typeof window !== 'undefined') {
      return localStorage.getItem('galacticAdminMode') === 'true';
    }
    return false;
  });

  const notificationRef = useRef<HTMLDivElement>(null);
  const userMenuRef = useRef<HTMLDivElement>(null);
  const settingsRef = useRef<HTMLDivElement>(null);

  // Update localStorage when modes change and dispatch custom event
  useEffect(() => {
    if (typeof window !== 'undefined') {
      localStorage.setItem('adminMode', adminMode.toString());
      // Dispatch custom event to notify other components
      window.dispatchEvent(new Event('localStorageChange'));
    }
  }, [adminMode]);

  useEffect(() => {
    if (typeof window !== 'undefined') {
      localStorage.setItem('galacticAdminMode', galacticAdminMode.toString());
      // Dispatch custom event to notify other components
      window.dispatchEvent(new Event('localStorageChange'));
    }
  }, [galacticAdminMode]);

  // Close dropdowns when clicking outside
  useEffect(() => {
    function handleClickOutside(event: MouseEvent) {
      if (notificationRef.current && !notificationRef.current.contains(event.target as Node)) {
        setShowNotifications(false);
      }
      if (userMenuRef.current && !userMenuRef.current.contains(event.target as Node)) {
        setShowUserMenu(false);
      }
      if (settingsRef.current && !settingsRef.current.contains(event.target as Node)) {
        setShowSettings(false);
      }
    }

    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, []);

  const getNotificationIcon = (type: string) => {
    switch (type) {
      case 'error':
        return '🔴';
      case 'warning':
        return '⚠️';
      case 'success':
        return '✅';
      default:
        return 'ℹ️';
    }
  };

  const formatTime = (date: Date) => {
    const now = new Date();
    const diffMs = now.getTime() - new Date(date).getTime();
    const diffMins = Math.floor(diffMs / 60000);

    if (diffMins < 1) return 'Just now';
    if (diffMins < 60) return `${diffMins}m ago`;
    const diffHours = Math.floor(diffMins / 60);
    if (diffHours < 24) return `${diffHours}h ago`;
    return `${Math.floor(diffHours / 24)}d ago`;
  };

  if (!isAuthenticated) {
    return null;
  }

  return (
    <nav className="bg-white border-b border-gray-200 shadow-sm">
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
        <div className="flex justify-between h-16">
          {/* Logo and Title */}
          <div className="flex items-center">
            <Link href="/" className="flex items-center">
              <span className="text-2xl font-bold text-blue-600">Autopilot Monitor</span>
            </Link>
          </div>

          {/* Right side - Notifications, Settings, Help, Feedback, User */}
          <div className="flex items-center space-x-2">
            {/* Notification Bell */}
            <div className="relative" ref={notificationRef}>
              <button
                onClick={() => setShowNotifications(!showNotifications)}
                className="relative p-2 rounded-full hover:bg-gray-100 transition-colors"
              >
                <svg className="w-6 h-6 text-gray-600" fill="none" strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" viewBox="0 0 24 24" stroke="currentColor">
                  <path d="M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6.002 6.002 0 00-4-5.659V5a2 2 0 10-4 0v.341C7.67 6.165 6 8.388 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 11-6 0v-1m6 0H9"></path>
                </svg>
                {unreadCount > 0 && (
                  <span className="absolute -top-1 -right-1 inline-flex items-center justify-center px-2 py-1 text-xs font-bold leading-none text-white bg-red-600 rounded-full min-w-[20px] h-5">
                    {unreadCount > 9 ? '9+' : unreadCount}
                  </span>
                )}
              </button>

              {/* Notification Dropdown */}
              {showNotifications && (
                <div className="absolute right-0 mt-2 w-96 bg-white rounded-lg shadow-lg border border-gray-200 z-50 max-h-96 overflow-hidden flex flex-col">
                  <div className="px-4 py-3 border-b border-gray-200 flex justify-between items-center">
                    <h3 className="text-lg font-semibold text-gray-900">Notifications</h3>
                    {notifications.length > 0 && (
                      <div className="flex space-x-2">
                        {unreadCount > 0 && (
                          <button onClick={markAllAsRead} className="text-sm text-blue-600 hover:text-blue-800">
                            Mark all read
                          </button>
                        )}
                        <button onClick={clearAll} className="text-sm text-gray-600 hover:text-gray-800">
                          Clear all
                        </button>
                      </div>
                    )}
                  </div>
                  <div className="overflow-y-auto flex-1">
                    {notifications.length === 0 ? (
                      <div className="p-8 text-center text-gray-500">
                        <svg className="w-16 h-16 mx-auto mb-4 text-gray-300" fill="none" strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" viewBox="0 0 24 24" stroke="currentColor">
                          <path d="M20 13V6a2 2 0 00-2-2H6a2 2 0 00-2 2v7m16 0v5a2 2 0 01-2 2H6a2 2 0 01-2-2v-5m16 0h-2.586a1 1 0 00-.707.293l-2.414 2.414a1 1 0 01-.707.293h-3.172a1 1 0 01-.707-.293l-2.414-2.414A1 1 0 006.586 13H4"></path>
                        </svg>
                        <p>No notifications</p>
                      </div>
                    ) : (
                      <div className="divide-y divide-gray-200">
                        {notifications.map((notification) => (
                          <div key={notification.id} className={`p-4 hover:bg-gray-50 transition-colors cursor-pointer ${!notification.read ? 'bg-blue-50' : ''}`} onClick={() => { if (!notification.read) { markAsRead(notification.id); } }}>
                            <div className="flex items-start justify-between">
                              <div className="flex items-start space-x-3 flex-1">
                                <span className="text-2xl">{getNotificationIcon(notification.type)}</span>
                                <div className="flex-1 min-w-0">
                                  <p className="text-sm font-medium text-gray-900">{notification.title}</p>
                                  <p className="text-sm text-gray-600 mt-1">{notification.message}</p>
                                  <p className="text-xs text-gray-400 mt-1">{formatTime(notification.timestamp)}</p>
                                </div>
                              </div>
                              <button onClick={(e) => { e.stopPropagation(); removeNotification(notification.id); }} className="ml-2 text-gray-400 hover:text-gray-600">
                                <svg className="w-4 h-4" fill="none" strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" viewBox="0 0 24 24" stroke="currentColor">
                                  <path d="M6 18L18 6M6 6l12 12"></path>
                                </svg>
                              </button>
                            </div>
                          </div>
                        ))}
                      </div>
                    )}
                  </div>
                </div>
              )}
            </div>

            {/* Settings Menu */}
            <div className="relative" ref={settingsRef}>
              <button onClick={() => setShowSettings(!showSettings)} className="p-2 rounded-full hover:bg-gray-100 transition-colors" title="Settings">
                <svg className="w-6 h-6 text-gray-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
                </svg>
              </button>

              {/* Settings Dropdown */}
              {showSettings && (
                <div className="absolute right-0 mt-2 w-72 bg-white rounded-lg shadow-lg border border-gray-200 z-50 max-h-[32rem] overflow-y-auto">
                  <div className="p-4">
                    <h3 className="text-sm font-semibold text-gray-900 mb-3">Settings</h3>

                    {/* Admin Mode Toggle */}
                    <div className="mb-3">
                      <div className="flex items-center justify-between p-3 rounded-lg bg-gray-50">
                        <div className="flex items-center gap-2">
                          <span className="text-sm font-medium text-gray-700">Admin Mode</span>
                          {adminMode && <span className="text-xs text-amber-600 font-semibold">AKTIV</span>}
                        </div>
                        <button onClick={() => setAdminMode(!adminMode)} className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${adminMode ? 'bg-amber-500' : 'bg-gray-300'}`}>
                          <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${adminMode ? 'translate-x-6' : 'translate-x-1'}`} />
                        </button>
                      </div>
                    </div>

                    {/* Galactic Admin Toggle */}
                    {user?.isGalacticAdmin && (
                      <div className="mb-3">
                        <div className="flex items-center justify-between p-3 rounded-lg bg-purple-50">
                          <div className="flex items-center gap-2">
                            <span className="text-sm font-medium text-gray-700">Galactic Admin</span>
                            {galacticAdminMode && <span className="text-xs text-purple-700 font-semibold">ACTIVE</span>}
                          </div>
                          <button onClick={() => setGalacticAdminMode(!galacticAdminMode)} className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${galacticAdminMode ? 'bg-purple-600' : 'bg-gray-300'}`}>
                            <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${galacticAdminMode ? 'translate-x-6' : 'translate-x-1'}`} />
                          </button>
                        </div>
                        {galacticAdminMode && (
                          <p className="mt-2 text-xs text-purple-700 px-3">Shows ALL sessions across ALL tenants</p>
                        )}
                      </div>
                    )}

                    {/* Configuration */}
                    <div className="border-t border-gray-200 pt-3 mt-3">
                      <Link href="/settings" className="block w-full p-3 text-left rounded-lg hover:bg-gray-50 transition-colors" onClick={() => setShowSettings(false)}>
                        <div className="flex items-center justify-between">
                          <span className="text-sm font-medium text-gray-700">Configuration</span>
                          <svg className="h-5 w-5 text-gray-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z" />
                          </svg>
                        </div>
                      </Link>
                    </div>

                    {/* Usage Metrics */}
                    <div className="border-t border-gray-200 pt-3">
                      <Link href="/usage-metrics" className="block w-full p-3 text-left rounded-lg hover:bg-gray-50 transition-colors" onClick={() => setShowSettings(false)}>
                        <div className="flex items-center justify-between">
                          <span className="text-sm font-medium text-gray-700">Usage Metrics</span>
                          <svg className="h-5 w-5 text-gray-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z" />
                          </svg>
                        </div>
                      </Link>
                    </div>

                    {/* Admin Configuration - Galactic Admin Only */}
                    {galacticAdminMode && (
                      <div className="border-t border-purple-200 pt-3">
                        <Link href="/admin-configuration" className="block w-full p-3 text-left rounded-lg hover:bg-purple-50 transition-colors" onClick={() => setShowSettings(false)}>
                          <div className="flex items-center justify-between">
                            <span className="text-sm font-medium text-purple-700">Admin Configuration</span>
                            <svg className="h-5 w-5 text-purple-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 6V4m0 2a2 2 0 100 4m0-4a2 2 0 110 4m-6 8a2 2 0 100-4m0 4a2 2 0 110-4m0 4v2m0-6V4m6 6v10m6-2a2 2 0 100-4m0 4a2 2 0 110-4m0 4v2m0-6V4" />
                            </svg>
                          </div>
                        </Link>
                      </div>
                    )}

                    {/* Platform Usage Metrics - Galactic Admin Only */}
                    {galacticAdminMode && (
                      <div className="border-t border-purple-200 pt-3">
                        <Link href="/platform-usage-metrics" className="block w-full p-3 text-left rounded-lg hover:bg-purple-50 transition-colors" onClick={() => setShowSettings(false)}>
                          <div className="flex items-center justify-between">
                            <span className="text-sm font-medium text-purple-700">Platform Usage Metrics</span>
                            <svg className="h-5 w-5 text-purple-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3.055 11H5a2 2 0 012 2v1a2 2 0 002 2 2 2 0 012 2v2.945M8 3.935V5.5A2.5 2.5 0 0010.5 8h.5a2 2 0 012 2 2 2 0 104 0 2 2 0 012-2h1.064M15 20.488V18a2 2 0 012-2h3.064M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                            </svg>
                          </div>
                        </Link>
                      </div>
                    )}

                    {/* System Health Check - Galactic Admin Only */}
                    {galacticAdminMode && (
                      <div className="border-t border-purple-200 pt-3">
                        <Link href="/health-check" className="block w-full p-3 text-left rounded-lg hover:bg-purple-50 transition-colors" onClick={() => setShowSettings(false)}>
                          <div className="flex items-center justify-between">
                            <span className="text-sm font-medium text-purple-700">System Health Check</span>
                            <svg className="h-5 w-5 text-purple-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                            </svg>
                          </div>
                        </Link>
                      </div>
                    )}
                  </div>
                </div>
              )}
            </div>

            {/* Help Icon */}
            <Link href="/docs" className="p-2 rounded-full hover:bg-gray-100 transition-colors" title="Documentation">
              <svg className="w-6 h-6 text-gray-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8.228 9c.549-1.165 2.03-2 3.772-2 2.21 0 4 1.343 4 3 0 1.4-1.278 2.575-3.006 2.907-.542.104-.994.54-.994 1.093m0 3h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
            </Link>

            {/* Feedback Icon */}
            <a href="https://github.com/yourusername/autopilot-monitor/issues" target="_blank" rel="noopener noreferrer" className="p-2 rounded-full hover:bg-gray-100 transition-colors" title="Feedback">
              <svg className="w-6 h-6 text-gray-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 8h10M7 12h4m1 8l-4-4H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-3l-4 4z" />
              </svg>
            </a>

            {/* User Menu */}
            <div className="relative" ref={userMenuRef}>
              <button onClick={() => setShowUserMenu(!showUserMenu)} className="flex items-center space-x-2 p-2 rounded-lg hover:bg-gray-100 transition-colors">
                <div className="w-8 h-8 rounded-full bg-blue-600 flex items-center justify-center text-white font-semibold text-sm">
                  {(() => {
                    if (user?.displayName) {
                      const names = user.displayName.split(' ');
                      if (names.length >= 2) {
                        return `${names[0].charAt(0)}${names[names.length - 1].charAt(0)}`.toUpperCase();
                      }
                      return user.displayName.charAt(0).toUpperCase();
                    }
                    return user?.upn?.charAt(0).toUpperCase() || 'U';
                  })()}
                </div>
                <svg className="w-4 h-4 text-gray-600" fill="none" strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" viewBox="0 0 24 24" stroke="currentColor">
                  <path d="M19 9l-7 7-7-7"></path>
                </svg>
              </button>

              {/* User Dropdown */}
              {showUserMenu && (
                <div className="absolute right-0 mt-2 w-80 bg-white rounded-lg shadow-lg border border-gray-200 z-50">
                  <div className="px-4 py-3 border-b border-gray-200 flex items-start space-x-3">
                    <div className="w-10 h-10 rounded-full bg-blue-600 flex-shrink-0 flex items-center justify-center text-white font-semibold text-sm">
                      {(() => {
                        if (user?.displayName) {
                          const names = user.displayName.split(' ');
                          if (names.length >= 2) {
                            return `${names[0].charAt(0)}${names[names.length - 1].charAt(0)}`.toUpperCase();
                          }
                          return user.displayName.charAt(0).toUpperCase();
                        }
                        return user?.upn?.charAt(0).toUpperCase() || 'U';
                      })()}
                    </div>
                    <div className="min-w-0">
                      <p className="text-sm font-medium text-gray-900">{user?.displayName || 'User'}</p>
                      <p className="text-sm text-gray-600 truncate">{user?.upn}</p>
                      {user?.isGalacticAdmin && (
                        <span className="inline-block mt-2 px-2 py-1 text-xs font-semibold text-purple-800 bg-purple-100 rounded-full">
                          Galactic Admin
                        </span>
                      )}
                    </div>
                  </div>
                  <div className="py-1">
                    <button onClick={() => { logout(); setShowUserMenu(false); }} className="w-full text-left px-4 py-2 text-sm text-gray-700 hover:bg-gray-100 flex items-center space-x-2">
                      <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                        <path strokeLinecap="round" strokeLinejoin="round" d="M17 16l4-4m0 0l-4-4m4 4H7m6 4v1a3 3 0 01-3 3H6a3 3 0 01-3-3V7a3 3 0 013-3h4a3 3 0 013 3v1" />
                      </svg>
                      <span>Sign out</span>
                    </button>
                  </div>
                </div>
              )}
            </div>
          </div>
        </div>
      </div>
    </nav>
  );
}
