﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Diamond.Core.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prism.Events;
using VirtualZplPrinter.Client;
using VirtualZplPrinter.Events;
using VirtualZplPrinter.Models;

namespace VirtualZplPrinter.HostedServices
{
	public class TcpListenerService : HostedServiceTemplate
	{
		public TcpListenerService(ILogger<TcpListenerService> logger, IHostApplicationLifetime hostApplicationLifetime, IEventAggregator eventAggregator, IServiceScopeFactory serviceScopeFactory)
			: base(hostApplicationLifetime, logger, serviceScopeFactory)
		{
			this.EventAggregator = eventAggregator;
			this.ServiceScopeFactory = serviceScopeFactory;

			_ = this.EventAggregator.GetEvent<StartEvent>().Subscribe(async (e) =>
			  {
				  this.LabelConfiguration = e.LabelConfiguration;
				  this.IpAddress = e.IpAddress;
				  this.Port = e.Port;
				  this.ImagePathRoot = e.ImagePath;
				  _ = await this.StartListener();
			  }, ThreadOption.BackgroundThread);

			_ = this.EventAggregator.GetEvent<StopEvent>().Subscribe(async (e) =>
			  {
				  this.LabelConfiguration = null;
				  this.Port = 0;
				  this.ImagePathRoot = null;
				  await this.StopListener();
				  this.EventAggregator.GetEvent<RunningStateChangedEvent>().Publish(new RunningStateChangedEventArgs() { IsRunning = false });
			  }, ThreadOption.BackgroundThread);
		}

		protected IEventAggregator EventAggregator { get; set; }
		protected bool IsRunning { get; set; }
		protected LabelConfiguration LabelConfiguration { get; set; }
		protected IPAddress IpAddress { get; set; }
		protected int Port { get; set; }
		protected TcpListener Listener { get; set; }
		protected ManualResetEvent ResetEvent { get; } = new(false);
		protected string ImagePathRoot { get; set; }

		protected override void OnStarted()
		{
			_ = Task.Factory.StartNew(async () =>
			  {
				  using (IServiceScope scope = this.ServiceScopeFactory.CreateScope())
				  {
					  while (!this.CancellationToken.IsCancellationRequested)
					  {
						  try
						  {
							  //
							  // Hold here until enabled.
							  //
							  if (this.ResetEvent.WaitOne())
							  {
								  try
								  {
									  //
									  // Accept the connection.
									  //
									  TcpClient tcpClient = await this.Listener.AcceptTcpClientAsync();
									  
									  //
									  // Start the client.
									  //
									  TcpListenerClientHandler clientService = scope.ServiceProvider.GetRequiredService<TcpListenerClientHandler>();
									  _ = clientService.StartSessionAsync(tcpClient, this.LabelConfiguration, this.ImagePathRoot);
								  }
								  catch (SocketException)
								  {
								  }
								  catch (InvalidOperationException)
								  {
								  }
							  }
						  }
						  catch (Exception ex)
						  {
							  this.EventAggregator.GetEvent<RunningStateChangedEvent>().Publish(new RunningStateChangedEventArgs() { IsRunning = false, IsError = true, ErrorMessage = ex.Message });
						  }
					  }
				  }
			  });
		}

		protected Task<bool> StartListener()
		{
			bool returnValue = false;

			try
			{
				this.Listener = new TcpListener(this.IpAddress, this.Port);
				this.Listener.Start();
				_ = this.ResetEvent.Set();
				this.EventAggregator.GetEvent<RunningStateChangedEvent>().Publish(new RunningStateChangedEventArgs() { IsRunning = true });
				returnValue = true;
			}
			catch (SocketException socketEx)
			{
				string message = socketEx.Message;

				if (socketEx.SocketErrorCode == SocketError.AddressAlreadyInUse)
				{
					message = "Address/port already in use.";
				}

				this.EventAggregator.GetEvent<RunningStateChangedEvent>().Publish(new RunningStateChangedEventArgs() { IsRunning = false, IsError = true, ErrorMessage = message });
				_ = this.ResetEvent.Reset();
				returnValue = false;
			}
			catch (Exception ex)
			{
				this.EventAggregator.GetEvent<RunningStateChangedEvent>().Publish(new RunningStateChangedEventArgs() { IsRunning = false, IsError = true, ErrorMessage = ex.Message });
				_ = this.ResetEvent.Reset();
				returnValue = false;
			}

			return Task.FromResult(returnValue);
		}

		protected async Task<bool> StopListener()
		{
			bool returnValue = false;

			try
			{
				this.Listener.Stop();
				await Task.Delay(1);
				this.EventAggregator.GetEvent<RunningStateChangedEvent>().Publish(new RunningStateChangedEventArgs() { IsRunning = false });
			}
			catch (Exception ex)
			{
				this.EventAggregator.GetEvent<RunningStateChangedEvent>().Publish(new RunningStateChangedEventArgs() { IsRunning = false, IsError = true, ErrorMessage = ex.Message });
				returnValue = false;
			}
			finally
			{
				_ = this.ResetEvent.Reset();
				this.Listener = null;
			}

			return returnValue;
		}
	}
}
