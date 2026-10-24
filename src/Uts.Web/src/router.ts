import { createRouter, createWebHistory } from 'vue-router'
import HomeView from '@/views/HomeView.vue'
import TrackView from '@/views/TrackView.vue'
import NotFoundView from '@/views/NotFoundView.vue'
import AdminLayout from '@/admin/AdminLayout.vue'
import ShipmentsView from '@/admin/ShipmentsView.vue'
import ShipmentDetailView from '@/admin/ShipmentDetailView.vue'
import ClientsView from '@/admin/ClientsView.vue'
import CitiesView from '@/admin/CitiesView.vue'
import TripsView from '@/admin/TripsView.vue'

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'home', component: HomeView },
    { path: '/track/:number?', name: 'track', component: TrackView },
    {
      path: '/admin',
      component: AdminLayout,
      children: [
        { path: '', name: 'admin-shipments', component: ShipmentsView },
        { path: 'shipments/:id', name: 'admin-shipment-detail', component: ShipmentDetailView },
        { path: 'clients', name: 'admin-clients', component: ClientsView },
        { path: 'cities', name: 'admin-cities', component: CitiesView },
        { path: 'trips', name: 'admin-trips', component: TripsView },
      ],
    },
    { path: '/:pathMatch(.*)*', name: 'not-found', component: NotFoundView },
  ],
})
