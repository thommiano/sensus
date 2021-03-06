// Copyright 2014 The Rector & Visitors of the University of Virginia
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using SensusService;
using Xamarin.Forms;

namespace SensusUI
{
    public class App : Application
    {
        public SensusMainPage _sensusMainPage;

        public SensusMainPage SensusMainPage
        {
            get { return _sensusMainPage; }
        }

        public App()
        {
            _sensusMainPage = new SensusMainPage();

            MainPage = new NavigationPage(_sensusMainPage);
        }

        protected override void OnSleep()
        {
            base.OnSleep();

            SensusServiceHelper serviceHelper = UiBoundSensusServiceHelper.Get(false);  // OnSleep can be called before the activity has actually had a chance to start up and bind to the service.
            if (serviceHelper != null)
                serviceHelper.OnSleep();               
        }
    }
}
